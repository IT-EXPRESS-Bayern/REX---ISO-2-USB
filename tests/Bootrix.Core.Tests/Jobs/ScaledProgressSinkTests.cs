// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Jobs;

namespace Bootrix.Core.Tests.Jobs;

public class ScaledProgressSinkTests
{
    private sealed class Collector : IProgressSink
    {
        public List<ProgressReport> Reports { get; } = [];

        public void Report(in ProgressReport report) => Reports.Add(report);
    }

    private static ProgressReport At(double overall) => new("job", 2, 5, "Step", 0.5, overall, 10, 20, 1, null, "detail");

    [Theory]
    [InlineData(0, 0.1)]
    [InlineData(0.5, 0.55)]
    [InlineData(1, 1.0)]
    public void OverallProgressIsMovedIntoTheGivenRange(double inner, double expected)
    {
        var collector = new Collector();

        new ScaledProgressSink(collector, 0.1, 1.0).Report(At(inner));

        Assert.Equal(expected, Assert.Single(collector.Reports).OverallFraction, 6);
    }

    [Fact]
    public void EverythingElseIsPassedOnUnchanged()
    {
        var collector = new Collector();

        new ScaledProgressSink(collector, 0.2, 0.4).Report(At(0.5));

        var report = Assert.Single(collector.Reports);
        Assert.Equal(("job", 2, 5, "Step", 0.5, 10L, 20L, "detail"), (report.JobId, report.StepIndex, report.StepCount, report.StepKey, report.StepFraction, report.BytesDone, report.BytesTotal, report.Detail));
    }

    [Fact]
    public void ValuesOutsideTheRangeAreClamped()
    {
        var collector = new Collector();

        new ScaledProgressSink(collector, 0.2, 0.4).Report(At(1.7));

        Assert.Equal(0.4, Assert.Single(collector.Reports).OverallFraction, 6);
    }
}
