// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bootrix.Core.Jobs;
using Bootrix.Core.Localization;
using Bootrix.Core.Text;

namespace Bootrix.Core.Presentation;

/// <summary>Progress as it is shown to the user: one title line, a percentage, speed and remaining time.</summary>
public sealed record ProgressView(string Title, double Percent, string Speed, string Remaining)
{
    /// <summary>Detail value a step sets while it re-reads what it wrote, so the title can say so.</summary>
    public const string VerifyDetail = "verify";

    public static ProgressView From(in ProgressReport report, Localizer localizer)
    {
        var key = "Step." + report.StepKey;
        if (report.Detail == VerifyDetail && localizer.Has(key + ".Verify"))
        {
            key += ".Verify";
        }

        var title = report.StepCount > 1
            ? string.Create(CultureInfo.InvariantCulture, $"{report.StepIndex + 1}/{report.StepCount}  {localizer.Get(key)}")
            : localizer.Get(key);

        return new ProgressView(
            title,
            Math.Round(report.OverallFraction * 100, 1),
            ByteSize.FormatRate(report.BytesPerSecond, localizer.Culture),
            ByteSize.FormatDuration(report.Eta));
    }
}
