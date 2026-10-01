// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Net;

namespace Bootrix.Core.Tests.Net;

public class SegmentSchedulerTests
{
    private const int Kib = 1024;

    private static SegmentScheduler Create(long length, int workers, long minSplit, RangeSet? done = null, Func<long, long>? align = null) =>
        new(length, done ?? new RangeSet(), workers, minSplit, align);

    private static List<Segment> TakeAll(SegmentScheduler scheduler)
    {
        var taken = new List<Segment>();
        while (scheduler.HasPending)
        {
            taken.Add(scheduler.TryTake()!);
        }

        return taken;
    }

    [Fact]
    public void FreshDownloadIsPlannedAsOneSegmentPerWorker()
    {
        var scheduler = Create(8000, workers: 4, minSplit: 100);

        var segments = TakeAll(scheduler);

        Assert.Equal(4, segments.Count);
        Assert.Equal([0L, 2000L, 4000L, 6000L], segments.Select(s => s.Start));
        Assert.Equal([2000L, 4000L, 6000L, 8000L], segments.Select(s => s.End));
    }

    [Fact]
    public void SegmentsNeverGetSmallerThanTheMinimumAtPlanningTime()
    {
        var scheduler = Create(1000, workers: 8, minSplit: 400);

        var segments = TakeAll(scheduler);

        Assert.Equal(2, segments.Count);
        Assert.Equal(1000, segments.Sum(s => s.End - s.Start));
    }

    [Fact]
    public void PlanningCoversExactlyTheMissingGaps()
    {
        var done = RangeSet.From([new ByteRange(0, 3000), new ByteRange(5000, 6000)]);
        var scheduler = Create(10_000, workers: 3, minSplit: 100, done);

        var segments = TakeAll(scheduler);

        var planned = RangeSet.From(segments.Select(s => new ByteRange(s.Start, s.End)));
        Assert.Equal([new ByteRange(3000, 5000), new ByteRange(6000, 10_000)], planned.Ranges);
        Assert.Equal(6000, planned.Total);
    }

    [Fact]
    public void NothingToPlanWhenEverythingIsDone()
    {
        var scheduler = Create(100, workers: 4, minSplit: 10, RangeSet.From([new ByteRange(0, 100)]));

        Assert.True(scheduler.IsComplete);
        Assert.Null(scheduler.TryTake());
    }

    [Fact]
    public void IdleWorkerTakesTheSecondHalfOfTheLargestRunningSegment()
    {
        var scheduler = Create(1000, workers: 1, minSplit: 100);
        var victim = scheduler.TryTake()!;

        var thief = scheduler.TryTake()!;

        Assert.Equal((0L, 500L), (victim.Start, victim.End));
        Assert.Equal((500L, 1000L), (thief.Start, thief.End));
    }

    [Fact]
    public void SplitRespectsWhatTheVictimAlreadyClaimed()
    {
        var scheduler = Create(1000, workers: 1, minSplit: 100);
        var victim = scheduler.TryTake()!;
        scheduler.Claim(victim, 400, out _);
        scheduler.Commit(victim, 400);

        var thief = scheduler.TryTake()!;

        Assert.Equal(700, thief.Start);
        Assert.Equal(700, victim.End);
        Assert.Equal(1000, thief.End);
    }

    [Fact]
    public void SegmentsBelowTwiceTheMinimumAreNotSplit()
    {
        var scheduler = Create(1000, workers: 1, minSplit: 600);
        scheduler.TryTake();

        Assert.Null(scheduler.TryTake());
    }

    [Fact]
    public void SplitPointIsAlignedDownToAPieceBoundary()
    {
        var scheduler = Create(10 * Kib, workers: 1, minSplit: Kib, align: offset => offset / (4 * Kib) * (4 * Kib));
        var victim = scheduler.TryTake()!;

        var thief = scheduler.TryTake()!;

        Assert.Equal(4 * Kib, thief.Start);
        Assert.Equal(4 * Kib, victim.End);
    }

    [Fact]
    public void NoSplitWhenAlignmentWouldLeaveNothingForTheVictim()
    {
        var scheduler = Create(10 * Kib, workers: 1, minSplit: Kib, align: _ => 0);
        scheduler.TryTake();

        Assert.Null(scheduler.TryTake());
    }

    [Fact]
    public void ClaimIsCutAtTheCurrentEndOfASplitSegment()
    {
        var scheduler = Create(1000, workers: 1, minSplit: 100);
        var victim = scheduler.TryTake()!;
        scheduler.TryTake();

        var claimed = scheduler.Claim(victim, 800, out var offset);

        Assert.Equal(500, claimed);
        Assert.Equal(0, offset);
        Assert.Equal(0, scheduler.Claim(victim, 10, out _));
    }

    [Fact]
    public void CompletedSegmentsBecomeDoneAndTheDownloadCompletes()
    {
        var scheduler = Create(1000, workers: 2, minSplit: 100);
        var a = scheduler.TryTake()!;
        var b = scheduler.TryTake()!;

        scheduler.Complete(a);
        Assert.False(scheduler.IsComplete);
        scheduler.Complete(b);

        Assert.True(scheduler.IsComplete);
        Assert.Equal(0, scheduler.Missing);
    }

    [Fact]
    public void ReleasedSegmentReturnsOnlyTheUnwrittenRest()
    {
        var scheduler = Create(1000, workers: 1, minSplit: 2000);
        var segment = scheduler.TryTake()!;
        scheduler.Claim(segment, 300, out _);
        scheduler.Commit(segment, 300);

        scheduler.Release(segment);
        var again = scheduler.TryTake()!;

        Assert.Equal((300L, 1000L), (again.Start, again.End));
        Assert.Equal([new ByteRange(0, 300)], scheduler.Snapshot().Ranges);
    }

    [Fact]
    public void ReopenedRangeBecomesWorkAgain()
    {
        var scheduler = Create(1000, workers: 1, minSplit: 2000);
        scheduler.Complete(scheduler.TryTake()!);
        Assert.True(scheduler.IsComplete);

        scheduler.Reopen(new ByteRange(200, 400));

        Assert.False(scheduler.IsComplete);
        Assert.Equal(200, scheduler.Missing);
        var segment = scheduler.TryTake()!;
        Assert.Equal((200L, 400L), (segment.Start, segment.End));
    }

    [Fact]
    public void SnapshotIncludesWhatRunningSegmentsHaveWritten()
    {
        var scheduler = Create(1000, workers: 2, minSplit: 100);
        var a = scheduler.TryTake()!;
        var b = scheduler.TryTake()!;
        scheduler.Claim(a, 120, out _);
        scheduler.Commit(a, 120);
        scheduler.Claim(b, 50, out _);

        var snapshot = scheduler.Snapshot();

        // b claimed but has not committed: those bytes are not on disk yet and must not be reported.
        Assert.Equal([new ByteRange(0, 120)], snapshot.Ranges);
    }

    [Fact]
    public async Task ConcurrentWorkersFillEveryByteExactlyOnce()
    {
        const int length = 3_000_011;
        var written = new int[length];
        var scheduler = Create(length, workers: 8, minSplit: 20_000, align: offset => offset / 4096 * 4096);

        async Task WorkerAsync(int seed)
        {
            var random = new Random(seed);
            while (scheduler.TryTake() is { } segment)
            {
                while (true)
                {
                    var claimed = scheduler.Claim(segment, random.Next(1, 9000), out var offset);
                    if (claimed == 0)
                    {
                        break;
                    }

                    for (var i = 0; i < claimed; i++)
                    {
                        Interlocked.Increment(ref written[offset + i]);
                    }

                    if (random.Next(4) == 0)
                    {
                        await Task.Yield();
                    }

                    scheduler.Commit(segment, claimed);
                }

                scheduler.Complete(segment);
            }
        }

        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() => WorkerAsync(i))));

        Assert.True(scheduler.IsComplete);
        Assert.Equal(0, written.Count(count => count != 1));
    }
}
