// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Jobs;

/// <summary>
/// Maps the overall progress of a job into a part of a larger run, so that work done before and after the job
/// (copying files in and out) shares one progress bar with it.
/// </summary>
public sealed class ScaledProgressSink(IProgressSink inner, double from, double to) : IProgressSink
{
    public void Report(in ProgressReport report)
    {
        var overall = from + (to - from) * Math.Clamp(report.OverallFraction, 0, 1);
        inner.Report(report with { OverallFraction = overall });
    }
}
