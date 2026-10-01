// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Jobs;

public interface IJob
{
    string Id { get; }

    string Title { get; }

    IReadOnlyList<IJobStep> Steps { get; }
}

public interface IJobStep
{
    /// <summary>Resource key of the step title, used for localized progress text.</summary>
    string Key { get; }

    /// <summary>Relative share of the whole job; only the ratio between steps matters.</summary>
    double Weight { get; }

    Task ExecuteAsync(JobContext context, CancellationToken cancellationToken);
}

public sealed class DelegateJobStep(string key, double weight, Func<JobContext, CancellationToken, Task> body) : IJobStep
{
    public string Key => key;

    public double Weight => weight;

    public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken) => body(context, cancellationToken);
}

public sealed class Job(string id, string title, IReadOnlyList<IJobStep> steps) : IJob
{
    public string Id => id;

    public string Title => title;

    public IReadOnlyList<IJobStep> Steps => steps;
}
