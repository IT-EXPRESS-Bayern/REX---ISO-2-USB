// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bootrix.Core.Localization;
using Bootrix.Core.Presentation;
using Bootrix.Core.Storage.Testing;

namespace Bootrix.Core.Tests.Presentation;

public class StickTestViewTests
{
    private static readonly Localizer English = new() { Culture = CultureInfo.GetCultureInfo("en-US") };
    private static readonly Localizer German = new() { Culture = CultureInfo.GetCultureInfo("de-DE") };

    private const long Gb = 1L << 30;

    private static CapacityProbeResult Genuine() => new(true, 64 * Gb, 64 * Gb, null, 4096, 0, 22.5, 85);

    private static StickTestReport Report(CapacityProbeResult capacity, BadBlockResult? blocks = null) =>
        new("USB DISK 3.0", "0123456789", blocks is null ? StickTestMode.Capacity : StickTestMode.Quick, capacity, blocks);

    [Fact]
    public void AGoodStickWithABlockTestIsGood()
    {
        var summary = StickTestView.Describe(Report(Genuine(), new BadBlockResult(2, [], 0, 0, 64 * Gb)), English);

        Assert.Equal(StickVerdictKind.Good, summary.Verdict);
        Assert.Contains("no bad blocks", summary.VerdictText, StringComparison.Ordinal);
        Assert.Contains(summary.Lines, l => l.Label == "Passes of the block test" && l.Value == "2");
    }

    [Fact]
    public void WithoutABlockTestOnlyTheCapacityIsConfirmed()
    {
        var summary = StickTestView.Describe(Report(Genuine()), German);

        Assert.Equal(StickVerdictKind.CapacityOnly, summary.Verdict);
        Assert.Contains("nicht geprüft", summary.VerdictText, StringComparison.Ordinal);
    }

    [Fact]
    public void AFakeStickIsReportedWithItsRealSize()
    {
        var capacity = new CapacityProbeResult(false, 1024 * Gb, 8 * Gb, 8 * Gb, 16384, 9000, 5, 10);

        var summary = StickTestView.Describe(Report(capacity), English);

        Assert.Equal(StickVerdictKind.FakeCapacity, summary.Verdict);
        Assert.Equal("Faked capacity: the stick claims 1 TB but only has about 8 GB.", summary.VerdictText);
    }

    [Fact]
    public void AFakeStickWithoutAnEstimateStillSaysSo()
    {
        var capacity = new CapacityProbeResult(false, 64 * Gb, null, 0, 4096, 4000, 5, 10);

        Assert.Equal(StickVerdictKind.FakeCapacity, StickTestView.Describe(Report(capacity), English).Verdict);
    }

    [Fact]
    public void BadBlocksAreCountedAndSummedUp()
    {
        var blocks = new BadBlockResult(2, [new BadRange(4096, 4096), new BadRange(1 << 20, 8192)], 1, 1, 64 * Gb);

        var summary = StickTestView.Describe(Report(Genuine(), blocks), English);

        Assert.Equal(StickVerdictKind.BadBlocks, summary.Verdict);
        Assert.Contains("2 area(s) totalling 12 KB", summary.VerdictText, StringComparison.Ordinal);
        Assert.Contains(summary.Lines, l => l.Label == "Read and write errors" && l.Value == "2");
    }

    [Fact]
    public void TheReportsSurviveTheTripThroughJson()
    {
        var reports = new[] { Report(Genuine(), new BadBlockResult(2, [new BadRange(512, 512)], 0, 1, 64 * Gb)) };

        var back = StickTestReport.Deserialize(StickTestReport.Serialize(reports));

        var report = Assert.Single(back);
        Assert.Equal("USB DISK 3.0", report.Device);
        Assert.Equal(StickTestMode.Quick, report.Mode);
        Assert.Equal(64 * Gb, report.Capacity.ClaimedBytes);
        Assert.Equal(new BadRange(512, 512), Assert.Single(report.BadBlocks!.BadRanges));
        Assert.False(report.IsGood);
    }
}
