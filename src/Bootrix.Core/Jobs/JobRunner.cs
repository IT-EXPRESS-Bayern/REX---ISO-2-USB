// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Bootrix.Core.Jobs;

public sealed class JobRunner(ILogger<JobRunner> logger, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<JobResult> RunAsync(
        IJob job,
        IProgressSink? sink = null,
        CancellationToken cancellationToken = default,
        CancellationToken abortToken = default)
    {
        sink ??= NullProgressSink.Instance;
        var started = Stopwatch.GetTimestamp();
        var steps = job.Steps;
        var totalWeight = steps.Sum(s => s.Weight);
        if (totalWeight <= 0)
        {
            totalWeight = 1;
        }

        var speed = new SpeedEstimator(_time);
        var index = 0;
        double weightBefore = 0;
        long lastBytes = 0;
        long lastTotal = 0;

        void ReportStep(double fraction, long bytesDone, long bytesTotal, string? detail)
        {
            if (index >= steps.Count)
            {
                return;
            }

            if (bytesTotal > 0)
            {
                speed.Update(bytesDone);
                lastBytes = bytesDone;
                lastTotal = bytesTotal;
            }

            var overall = (weightBefore + steps[index].Weight * fraction) / totalWeight;
            sink.Report(new ProgressReport(
                job.Id,
                index,
                steps.Count,
                steps[index].Key,
                fraction,
                Math.Clamp(overall, 0, 1),
                bytesDone,
                bytesTotal,
                speed.BytesPerSecond,
                speed.Remaining(bytesDone, bytesTotal),
                detail));
        }

        var context = new JobContext(job, logger, ReportStep, abortToken);
        logger.LogInformation("Job {JobId} '{Title}' started with {Steps} steps", job.Id, job.Title, steps.Count);

        string? failedStep = null;
        try
        {
            for (index = 0; index < steps.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var step = steps[index];
                failedStep = step.Key;
                speed.Reset();
                lastBytes = 0;
                lastTotal = 0;
                logger.LogInformation("Step {Index}/{Count}: {Step}", index + 1, steps.Count, step.Key);

                ReportStep(0, 0, 0, null);
                await step.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
                ReportStep(1, lastTotal, lastTotal, null);
                weightBefore += step.Weight;
            }

            index = steps.Count - 1;
            var finished = Stopwatch.GetElapsedTime(started);
            logger.LogInformation("Job {JobId} finished in {Duration}", job.Id, finished);
            return new JobResult(JobOutcome.Succeeded, finished) { Values = context.Snapshot() };
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested || abortToken.IsCancellationRequested)
        {
            logger.LogWarning("Job {JobId} canceled during step {Step}", job.Id, failedStep);
            return new JobResult(JobOutcome.Canceled, Stopwatch.GetElapsedTime(started), ex, failedStep) { Values = context.Snapshot() };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Job {JobId} failed during step {Step}", job.Id, failedStep);
            return new JobResult(JobOutcome.Failed, Stopwatch.GetElapsedTime(started), ex, failedStep) { Values = context.Snapshot() };
        }
        finally
        {
            await context.RunCleanupsAsync().ConfigureAwait(false);
        }
    }
}
