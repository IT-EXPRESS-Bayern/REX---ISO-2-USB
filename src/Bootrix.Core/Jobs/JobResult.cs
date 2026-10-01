// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;

namespace Bootrix.Core.Jobs;

public enum JobOutcome
{
    Succeeded,
    Canceled,
    Failed,
}

public sealed record JobResult(JobOutcome Outcome, TimeSpan Duration, Exception? Error = null, string? FailedStep = null)
{
    /// <summary>Values the steps stored in the job context, such as the write report.</summary>
    public IReadOnlyDictionary<string, object?> Values { get; init; } = new Dictionary<string, object?>();

    public bool Succeeded => Outcome == JobOutcome.Succeeded;

    public ErrorDescription? Describe(Localization.Localizer? localizer = null) =>
        Error is null ? null : ErrorCatalog.Describe(Error, localizer);
}
