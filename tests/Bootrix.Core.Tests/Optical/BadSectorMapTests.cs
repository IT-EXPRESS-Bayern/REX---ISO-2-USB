// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Optical.Reading;

namespace Bootrix.Core.Tests.Optical;

public class BadSectorMapTests
{
    [Fact]
    public void ConsecutiveSectorsFormOneRange()
    {
        var map = new BadSectorMap();
        foreach (var lba in new long[] { 10, 11, 12, 13 })
        {
            map.Add(lba);
        }

        Assert.Equal([new SectorRange(10, 4)], map.Ranges);
        Assert.Equal(4, map.Count);
    }

    [Fact]
    public void GapsStartNewRanges()
    {
        var map = new BadSectorMap();
        foreach (var lba in new long[] { 5, 6, 100, 200, 201 })
        {
            map.Add(lba);
        }

        Assert.Equal([new SectorRange(5, 2), new SectorRange(100, 1), new SectorRange(200, 2)], map.Ranges);
        Assert.Equal("5-6, 100, 200-201", map.Describe());
    }

    [Fact]
    public void SectorsAddedOutOfOrderAreMerged()
    {
        var map = new BadSectorMap();
        foreach (var lba in new long[] { 10, 30, 12, 11, 29, 20, 31 })
        {
            map.Add(lba);
        }

        Assert.Equal([new SectorRange(10, 3), new SectorRange(20, 1), new SectorRange(29, 3)], map.Ranges);
    }

    [Fact]
    public void FillingAGapJoinsBothNeighbours()
    {
        var map = new BadSectorMap();
        map.Add(10);
        map.Add(12);
        map.Add(11);

        Assert.Equal([new SectorRange(10, 3)], map.Ranges);
    }

    [Fact]
    public void AddingAKnownSectorChangesNothing()
    {
        var map = new BadSectorMap();
        map.Add(5);
        map.Add(6);
        map.Add(6);
        map.Add(5);

        Assert.Equal([new SectorRange(5, 2)], map.Ranges);
    }

    [Theory]
    [InlineData(9, false)]
    [InlineData(10, true)]
    [InlineData(12, true)]
    [InlineData(13, false)]
    [InlineData(50, true)]
    [InlineData(51, false)]
    [InlineData(0, false)]
    public void ContainsFindsMembers(long lba, bool expected)
    {
        var map = new BadSectorMap([new SectorRange(10, 3), new SectorRange(50, 1)]);

        Assert.Equal(expected, map.Contains(lba));
    }

    [Fact]
    public void RangesFromACheckpointAreNormalised()
    {
        var map = new BadSectorMap([new SectorRange(20, 5), new SectorRange(10, 5), new SectorRange(15, 5), new SectorRange(30, 0)]);

        Assert.Equal([new SectorRange(10, 15)], map.Ranges);
    }

    [Fact]
    public void EmptyMapHasNothing()
    {
        var map = new BadSectorMap();

        Assert.True(map.IsEmpty);
        Assert.Equal(0, map.Count);
        Assert.Equal("", map.Describe());
        Assert.False(map.Contains(0));
    }
}
