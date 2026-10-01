// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Optical;

namespace Bootrix.Core.Tests.Optical;

public class BurnProgressTests
{
    private static BurnProgress Event(int action, int start, int count, int lastWritten, int elapsed = 10, int remaining = 20, int total = 100, int used = 50) =>
        BurnProgress.FromWriteEvent(action, start, count, lastWritten, elapsed, remaining, total, used);

    [Fact]
    public void WritingProgressIsTheShareOfSectorsAlreadyWritten()
    {
        // an image of 1000 sectors written to the start of a blank disc: 250 done
        var progress = Event(4, 0, 1000, 250);

        Assert.Equal(BurnPhase.Writing, progress.Phase);
        Assert.Equal(250, progress.SectorsWritten);
        Assert.Equal(0.25, progress.Fraction, 6);
        Assert.Equal(TimeSpan.FromSeconds(20), progress.Remaining);
        Assert.Equal(50, progress.BufferFillPercent);
    }

    [Fact]
    public void CountersAreRelativeToTheStartOfTheWrite()
    {
        // appended session: starts at LBA 40000, 1000 sectors long, 500 written
        var progress = Event(4, 40_000, 1000, 40_500);

        Assert.Equal(500, progress.SectorsWritten);
        Assert.Equal(0.5, progress.Fraction, 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PhasesBeforeTheWriteCountAsNothingDone(int action)
    {
        Assert.Equal(0, Event(action, 0, 1000, -1).Fraction);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void PhasesAfterTheWriteCountAsDone(int action)
    {
        Assert.Equal(1, Event(action, 0, 1000, 999).Fraction);
    }

    [Fact]
    public void FractionStaysInRangeWhenTheDriveReportsOddValues()
    {
        Assert.Equal(1, Event(4, 0, 1000, 5000).Fraction);
        Assert.Equal(0, Event(4, 0, 1000, -150).Fraction);
        Assert.Equal(0, Event(4, 0, 0, 0).Fraction);
    }

    [Fact]
    public void UnknownActionFallsBackToValidating()
    {
        Assert.Equal(BurnPhase.ValidatingMedia, Event(42, 0, 100, 0).Phase);
    }

    [Fact]
    public void MissingTimeEstimateIsNull()
    {
        Assert.Null(Event(4, 0, 100, 10, remaining: 0).Remaining);
        Assert.Null(Event(4, 0, 100, 10, remaining: -1).Remaining);
    }

    [Fact]
    public void BufferFillHandlesAnEmptyBuffer()
    {
        Assert.Equal(0, Event(4, 0, 100, 10, total: 0, used: 0).BufferFillPercent);
        Assert.Equal(100, Event(4, 0, 100, 10, total: 10, used: 40).BufferFillPercent);
    }

    [Fact]
    public void PhaseNumbersMatchImapi()
    {
        // IMAPI_FORMAT2_DATA_WRITE_ACTION
        Assert.Equal(4, (int)BurnPhase.Writing);
        Assert.Equal(5, (int)BurnPhase.Finalizing);
        Assert.Equal(7, (int)BurnPhase.Verifying);
    }

    [Fact]
    public void EraseProgressIsElapsedOverEstimate()
    {
        var progress = new EraseProgress(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(120));

        Assert.Equal(0.25, progress.Fraction, 6);
        Assert.Equal(TimeSpan.FromSeconds(90), progress.Remaining);
        Assert.Equal(1, new EraseProgress(TimeSpan.FromSeconds(200), TimeSpan.FromSeconds(120)).Fraction);
        Assert.Null(new EraseProgress(TimeSpan.FromSeconds(5), TimeSpan.Zero).Remaining);
    }

    [Fact]
    public void SeveralDrivesAreAveraged()
    {
        var all = new MultiDriveProgress(3);
        all.Update(0, Event(4, 0, 1000, 600));
        all.Update(1, Event(4, 0, 1000, 300));

        // the third drive has not reported, it counts as 0
        Assert.Equal(0.3, all.Fraction, 6);
        Assert.Equal(900, all.SectorsWritten);
        Assert.Equal(2000, all.SectorsTotal);
    }

    [Fact]
    public void SlowestPhaseAndLongestTimeWin()
    {
        var all = new MultiDriveProgress(2);
        all.Update(0, Event(4, 0, 1000, 500, remaining: 30));
        all.Update(1, Event(3, 0, 1000, 0, remaining: 90));

        Assert.Equal(BurnPhase.CalibratingPower, all.Phase);
        Assert.Equal(TimeSpan.FromSeconds(90), all.Remaining);
    }

    [Fact]
    public void NoEstimateFromOneDriveMeansNoEstimateOverall()
    {
        var all = new MultiDriveProgress(2);
        all.Update(0, Event(4, 0, 1000, 500, remaining: 30));
        all.Update(1, Event(4, 0, 1000, 500, remaining: 0));

        Assert.Null(all.Remaining);
    }

    [Fact]
    public void UpdatesFromManyThreadsAreSafe()
    {
        var all = new MultiDriveProgress(8);
        Parallel.For(0, 8, drive =>
        {
            for (var sector = 0; sector <= 1000; sector += 10)
            {
                all.Update(drive, Event(4, 0, 1000, sector));
                _ = all.Fraction;
            }
        });

        Assert.Equal(1.0, all.Fraction, 6);
    }
}
