// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Storage;

namespace Bootrix.Core.Tests.Storage;

public sealed class SparseWriteMapTests
{
    [Fact]
    public void Ranges_AreSortedAndMerged()
    {
        var map = new SparseWriteMap([new ByteRange(8192, 4096), new ByteRange(0, 4096), new ByteRange(4096, 4096), new ByteRange(40960, 0)]);

        Assert.Equal([new ByteRange(0, 12288)], map.Ranges);
        Assert.Equal(12288, map.MappedBytes);
    }

    [Fact]
    public void Select_WidensPiecesToTheAlignment()
    {
        var map = new SparseWriteMap([new ByteRange(1000, 200), new ByteRange(5000, 100)]);

        var pieces = map.Select(0, 8192, 512);

        Assert.Equal([new ByteRange(512, 1024), new ByteRange(4608, 512)], pieces);
    }

    [Fact]
    public void Select_MergesPiecesThatTouchAfterWidening()
    {
        var map = new SparseWriteMap([new ByteRange(100, 50), new ByteRange(600, 50)]);

        Assert.Equal([new ByteRange(0, 1024)], map.Select(0, 4096, 512));
    }

    [Fact]
    public void Select_CutsRangesAtTheWindow()
    {
        var map = new SparseWriteMap([new ByteRange(0, 100_000)]);

        Assert.Equal([new ByteRange(4096, 4096)], map.Select(4096, 4096, 512));
        Assert.Empty(map.Select(200_000, 4096, 512));
    }

    [Fact]
    public void ClearGaps_ZeroesEverythingOutsideTheRanges()
    {
        var map = new SparseWriteMap([new ByteRange(10, 5), new ByteRange(30, 5)]);
        var data = Enumerable.Repeat((byte)0xAA, 50).ToArray();

        map.ClearGaps(0, data);

        var expected = new byte[50];
        Array.Fill(expected, (byte)0xAA, 10, 5);
        Array.Fill(expected, (byte)0xAA, 30, 5);
        Assert.Equal(expected, data);
    }

    [Fact]
    public void ClearGaps_HandlesAWindowInTheMiddle()
    {
        var map = new SparseWriteMap([new ByteRange(100, 100)]);
        var data = Enumerable.Repeat((byte)1, 100).ToArray();

        map.ClearGaps(150, data);

        Assert.All(data[..50], b => Assert.Equal(1, b));
        Assert.All(data[50..], b => Assert.Equal(0, b));
    }
}
