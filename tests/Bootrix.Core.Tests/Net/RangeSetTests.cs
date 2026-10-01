// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Net;

namespace Bootrix.Core.Tests.Net;

public class RangeSetTests
{
    private static RangeSet Of(params (long Start, long End)[] ranges) =>
        RangeSet.From(ranges.Select(r => new ByteRange(r.Start, r.End)));

    private static (long, long)[] Listed(RangeSet set) => [.. set.Ranges.Select(r => (r.Start, r.End))];

    [Fact]
    public void DisjointRangesStaySeparateAndSorted()
    {
        var set = Of((20, 30), (0, 5), (10, 12));

        Assert.Equal([(0L, 5L), (10L, 12L), (20L, 30L)], Listed(set));
        Assert.Equal(17, set.Total);
    }

    [Fact]
    public void AdjacentAndOverlappingRangesAreMerged()
    {
        var set = Of((0, 5), (5, 8), (7, 10), (20, 25), (3, 22));

        Assert.Equal([(0L, 25L)], Listed(set));
        Assert.Equal(25, set.Total);
    }

    [Fact]
    public void AddingARangeThatIsAlreadyCoveredChangesNothing()
    {
        var set = Of((0, 100));

        set.Add(10, 20);
        set.Add(0, 100);

        Assert.Equal([(0L, 100L)], Listed(set));
        Assert.Equal(100, set.Total);
    }

    [Fact]
    public void EmptyRangesAreIgnored()
    {
        var set = new RangeSet();

        set.Add(5, 5);
        set.Add(9, 3);

        Assert.Empty(set.Ranges);
        Assert.Equal(0, set.Total);
    }

    [Fact]
    public void RemovingTheMiddleSplitsARange()
    {
        var set = Of((0, 100));

        set.Remove(40, 60);

        Assert.Equal([(0L, 40L), (60L, 100L)], Listed(set));
        Assert.Equal(80, set.Total);
    }

    [Fact]
    public void RemovingAcrossSeveralRangesTrimsTheOutermost()
    {
        var set = Of((0, 10), (20, 30), (40, 50), (60, 70));

        set.Remove(5, 45);

        Assert.Equal([(0L, 5L), (45L, 50L), (60L, 70L)], Listed(set));
        Assert.Equal(20, set.Total);
    }

    [Fact]
    public void RemovingWhereNothingIsLeavesTheSetAlone()
    {
        var set = Of((10, 20));

        set.Remove(0, 10);
        set.Remove(20, 30);

        Assert.Equal([(10L, 20L)], Listed(set));
    }

    [Fact]
    public void CoversRequiresASingleRangeToContainTheWholeInterval()
    {
        var set = Of((0, 10), (20, 30));

        Assert.True(set.Covers(2, 8));
        Assert.True(set.Covers(20, 30));
        Assert.False(set.Covers(5, 25));
        Assert.False(set.Covers(10, 20));
        Assert.True(set.Covers(7, 7));
    }

    [Fact]
    public void GapsAreTheComplementWithinTheLength()
    {
        var set = Of((10, 20), (30, 40));

        Assert.Equal([new ByteRange(0, 10), new ByteRange(20, 30), new ByteRange(40, 50)], set.Gaps(50));
        Assert.Equal([new ByteRange(0, 10), new ByteRange(20, 25)], set.Gaps(25));
        Assert.Empty(Of((0, 50)).Gaps(50));
        Assert.Equal([new ByteRange(0, 7)], new RangeSet().Gaps(7));
    }

    [Fact]
    public void CloneIsIndependent()
    {
        var original = Of((0, 10));
        var clone = original.Clone();

        clone.Add(20, 30);

        Assert.Equal([(0L, 10L)], Listed(original));
        Assert.Equal([(0L, 10L), (20L, 30L)], Listed(clone));
    }

    [Fact]
    public void RandomOperationsAgreeWithABitmapModel()
    {
        const int size = 400;
        var random = new Random(1234);
        var model = new bool[size];
        var set = new RangeSet();

        for (var step = 0; step < 3000; step++)
        {
            var a = random.Next(size + 1);
            var b = random.Next(size + 1);
            var (start, end) = (Math.Min(a, b), Math.Max(a, b));
            var add = random.Next(3) != 0;

            if (add)
            {
                set.Add(start, end);
            }
            else
            {
                set.Remove(start, end);
            }

            for (var i = start; i < end; i++)
            {
                model[i] = add;
            }

            Assert.Equal(model.Count(x => x), set.Total);
            var expected = new List<(long, long)>();
            for (var i = 0; i < size; i++)
            {
                if (!model[i])
                {
                    continue;
                }

                var runEnd = i;
                while (runEnd < size && model[runEnd])
                {
                    runEnd++;
                }

                expected.Add((i, runEnd));
                i = runEnd;
            }

            Assert.Equal(expected, Listed(set));
        }
    }
}
