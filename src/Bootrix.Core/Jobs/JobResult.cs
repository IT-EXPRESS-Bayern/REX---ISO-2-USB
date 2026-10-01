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
    public bool Succeeded => Outcome == JobOutcome.Succeeded;

    public ErrorDescription? Describe(Localization.Localizer? localizer = null) =>
        Error is null ? null : ErrorCatalog.Describe(Error, localizer);
}
