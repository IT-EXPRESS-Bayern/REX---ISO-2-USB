// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Jobs;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Core.Tests.Optical.Support;

/// <summary>
/// Runs a job and keeps what a test wants to look at afterwards: the order of the steps, the progress reports and the
/// values the steps left in the job context under the given keys.
/// </summary>
internal sealed class JobHarness(params string[] valueKeys)
{
    private readonly string[] _valueKeys = valueKeys;

    public Dictionary<string, object?> Values { get; } = [];

    public List<string> StepKeys { get; } = [];

    public List<ProgressReport> Progress { get; } = [];

    public Task<JobResult> RunAsync(IJob job, CancellationToken cancellationToken = default)
    {
        var steps = job.Steps.Select(s => (IJobStep)new RecordingStep(s, this)).ToList();
        var runner = new JobRunner(NullLogger<JobRunner>.Instance);
        return runner.RunAsync(new Job(job.Id, job.Title, steps), new DelegateProgressSink(Progress.Add), cancellationToken);
    }

    private sealed class RecordingStep(IJobStep inner, JobHarness owner) : IJobStep
    {
        public string Key => inner.Key;

        public double Weight => inner.Weight;

        public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            owner.StepKeys.Add(inner.Key);
            try
            {
                await inner.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                // Read after the step even when it failed: a report of what did happen is most useful then.
                foreach (var key in owner._valueKeys)
                {
                    if (context.TryGet<object>(key, out var value) && value is not null)
                    {
                        owner.Values[key] = value;
                    }
                }
            }
        }
    }
}
