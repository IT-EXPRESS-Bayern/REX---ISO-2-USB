// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bootrix.Core.Localization;
using Bootrix.Core.Storage.Testing;
using Bootrix.Core.Text;

namespace Bootrix.Core.Presentation;

public enum StickVerdictKind
{
    Good,
    CapacityOnly,
    FakeCapacity,
    BadBlocks,
}

/// <summary>The findings for one stick in words: a verdict first, then the numbers behind it.</summary>
public sealed record StickTestSummary(string Title, StickVerdictKind Verdict, string VerdictText, IReadOnlyList<SummaryLine> Lines);

public static class StickTestView
{
    public static StickTestSummary Describe(StickTestReport report, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(localizer);

        var culture = localizer.Culture;
        var claimed = ByteSize.Format(report.Capacity.ClaimedBytes, culture);
        var (kind, text) = Verdict(report, claimed, localizer);

        var lines = new List<SummaryLine>
        {
            new(localizer.Get("Test.Line.Device"), string.IsNullOrWhiteSpace(report.Serial) ? report.Device : $"{report.Device} ({report.Serial})"),
            new(localizer.Get("Test.Line.Claimed"), claimed),
            new(localizer.Get("Test.Line.Write"), localizer.Get("Test.Speed", report.Capacity.WriteMegabytesPerSecond.ToString("0.#", culture))),
            new(localizer.Get("Test.Line.Read"), localizer.Get("Test.Speed", report.Capacity.ReadMegabytesPerSecond.ToString("0.#", culture))),
            new(localizer.Get("Test.Line.Probe"), string.Create(culture, $"{report.Capacity.ProbePoints:N0}")),
        };

        if (report.BadBlocks is { } blocks)
        {
            lines.Add(new SummaryLine(localizer.Get("Test.Line.Passes"), blocks.Passes.ToString(culture)));
            lines.Add(new SummaryLine(localizer.Get("Test.Line.Errors"), (blocks.ReadErrors + blocks.WriteErrors).ToString(culture)));
        }

        return new StickTestSummary(report.Device, kind, text, lines);
    }

    private static (StickVerdictKind Kind, string Text) Verdict(StickTestReport report, string claimed, Localizer localizer)
    {
        var culture = localizer.Culture;
        if (!report.Capacity.IsGenuine)
        {
            return report.Capacity.EstimatedRealBytes is { } real and > 0 && real < report.Capacity.ClaimedBytes
                ? (StickVerdictKind.FakeCapacity, localizer.Get("Test.Verdict.Fake", claimed, ByteSize.Format(real, culture)))
                : (StickVerdictKind.FakeCapacity, localizer.Get("Test.Verdict.FakeUnknown", claimed));
        }

        if (report.BadBlocks is { IsClean: false } bad)
        {
            var total = bad.BadRanges.Sum(r => r.Length);
            return (StickVerdictKind.BadBlocks, localizer.Get("Test.Verdict.Bad", bad.BadRanges.Count.ToString(culture), ByteSize.Format(total, culture)));
        }

        return report.BadBlocks is null
            ? (StickVerdictKind.CapacityOnly, localizer.Get("Test.Verdict.GoodCapacity"))
            : (StickVerdictKind.Good, localizer.Get("Test.Verdict.Good"));
    }
}
