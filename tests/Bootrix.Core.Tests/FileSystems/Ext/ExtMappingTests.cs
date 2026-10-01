// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Bootrix.Core.FileSystems.Ext;

namespace Bootrix.Core.Tests.FileSystems.Ext;

public class ExtMappingTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(12, 0)]
    [InlineData(13, 1)]
    [InlineData(268, 1)]
    [InlineData(269, 3)]
    [InlineData(65804, 258)]
    [InlineData(65805, 261)]
    public void BlockMap_CountsMappingBlocks(long dataBlocks, long expected)
    {
        Assert.Equal(expected, ExtBlockMap.MappingBlockCount(dataBlocks, 1024));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(268)]
    [InlineData(269)]
    [InlineData(65804)]
    [InlineData(65805)]
    [InlineData(70000)]
    public void BlockMap_BuildThenReadReturnsTheSameDataBlocks(long dataBlocks)
    {
        const int blockSize = 1024;
        var total = dataBlocks + ExtBlockMap.MappingBlockCount(dataBlocks, blockSize);
        var allocated = Enumerable.Range(0, (int)total).Select(i => 1000L + i * 2).ToArray();
        var written = new Dictionary<long, byte[]>();

        var iblock = ExtBlockMap.Build(allocated, dataBlocks, blockSize, (block, data) => written[block] = data, out var mapped);
        var read = ExtBlockMap.Read(iblock, dataBlocks, blockSize, (block, buffer) => written[block].CopyTo(buffer, 0));

        Assert.Equal(dataBlocks, mapped.Length);
        Assert.Equal(mapped, read);
        Assert.Equal(dataBlocks + ExtBlockMap.MappingBlockCount(dataBlocks, blockSize), mapped.Length + written.Count);
    }

    [Fact]
    public void BlockMap_RejectsFilesBeyondTripleIndirect()
    {
        Assert.Equal(12L + 256 + 256 * 256 + 256L * 256 * 256, ExtBlockMap.MaxDataBlocks(1024));
    }

    [Fact]
    public void ExtentTree_MergesContiguousBlocksAndSplitsAtMaximumLength()
    {
        var blocks = Enumerable.Range(0, 32768 + 10).Select(i => 5000L + i).Concat([90000L, 90001, 40000]).ToArray();

        var runs = ExtExtentTree.ToRuns(blocks);

        Assert.Equal(
            [new ExtentRun(0, 5000, 32768), new ExtentRun(32768, 5000 + 32768, 10), new ExtentRun(32778, 90000, 2), new ExtentRun(32780, 40000, 1)],
            runs);
    }

    [Fact]
    public void ExtentTree_InlineTreeRoundTrips()
    {
        var blocks = new long[] { 10, 11, 12, 20, 21, 30 };
        var iblock = ExtExtentTree.BuildInline(ExtExtentTree.ToRuns(blocks));

        var read = ExtExtentTree.Read(iblock, blocks.Length, 1024, (_, _) => Assert.Fail("No block should be read."));

        Assert.Equal(blocks, read);
    }

    [Fact]
    public void ExtentTree_EmptyTreeHasHeaderWithoutEntries()
    {
        var iblock = ExtExtentTree.BuildInline([]);

        Assert.Equal(0xF30A, BitConverter.ToUInt16(iblock, 0));
        Assert.Equal(0, BitConverter.ToUInt16(iblock, 2));
        Assert.Equal(4, BitConverter.ToUInt16(iblock, 4));
        Assert.Empty(ExtExtentTree.Read(iblock, 0, 1024, (_, _) => { }));
    }

    [Fact]
    public void ExtentTree_LeafRootRoundTrips()
    {
        var blocks = Enumerable.Range(0, 40).Select(i => 1000L + i * 3).ToArray();
        var runs = ExtExtentTree.ToRuns(blocks);
        var leaf = ExtExtentTree.BuildLeaf(runs, 1024);
        var root = ExtExtentTree.BuildIndexRoot([(0u, 777L)]);

        var read = ExtExtentTree.Read(root, blocks.Length, 1024, (block, buffer) =>
        {
            Assert.Equal(777, block);
            leaf.CopyTo(buffer, 0);
        });

        Assert.Equal(blocks, read);
        Assert.Equal(84, ExtExtentTree.LeafCapacity(1024));
        Assert.Equal(340, ExtExtentTree.LeafCapacity(4096));
    }

    [Fact]
    public void ExtentTree_RejectsDamagedHeader()
    {
        Assert.Throws<InvalidDataException>(() => ExtExtentTree.Read(new byte[60], 1, 1024, (_, _) => { }));
    }

    [Fact]
    public void Directory_BuildStretchesLastEntryToBlockEnd()
    {
        var block = ExtDirectory.Build(1024,
        [
            new ExtDirectoryEntry(2, ExtDirectory.TypeDirectory, "."),
            new ExtDirectoryEntry(2, ExtDirectory.TypeDirectory, ".."),
            new ExtDirectoryEntry(11, ExtDirectory.TypeDirectory, "lost+found"),
        ]);

        Assert.Equal(12, BitConverter.ToUInt16(block, 4));
        Assert.Equal(12, BitConverter.ToUInt16(block, 16));
        Assert.Equal(1024 - 24, BitConverter.ToUInt16(block, 28));
        Assert.True(ExtDirectory.Contains(block, "lost+found"u8));
        Assert.False(ExtDirectory.Contains(block, "lost"u8));
    }

    [Fact]
    public void Directory_TryAddUsesSlackUntilTheBlockIsFull()
    {
        var block = ExtDirectory.Build(1024, [new ExtDirectoryEntry(2, ExtDirectory.TypeDirectory, ".")]);
        var added = 0;

        while (ExtDirectory.HasRoomFor(block, 12))
        {
            var name = Encoding.UTF8.GetBytes($"file-{added:D7}");
            Assert.True(ExtDirectory.TryAdd(block, name, (uint)(100 + added), ExtDirectory.TypeRegular));
            added++;
        }

        Assert.Equal((1024 - 12) / 20, added);
        Assert.False(ExtDirectory.TryAdd(block, "overflow-name"u8, 5, ExtDirectory.TypeRegular));
        for (var i = 0; i < added; i++)
        {
            Assert.True(ExtDirectory.Contains(block, Encoding.UTF8.GetBytes($"file-{i:D7}")));
        }
    }

    [Fact]
    public void Directory_TryAddReusesUnusedRecord()
    {
        var block = ExtDirectory.EmptyBlock(1024);

        Assert.True(ExtDirectory.TryAdd(block, "a"u8, 12, ExtDirectory.TypeRegular));

        Assert.True(ExtDirectory.Contains(block, "a"u8));
        Assert.Equal(1024, BitConverter.ToUInt16(block, 4));
    }

    [Fact]
    public void Directory_DamagedRecordLengthIsRejected()
    {
        var block = new byte[1024];
        block[4] = 3;

        Assert.Throws<InvalidDataException>(() => ExtDirectory.Contains(block, "x"u8));
    }
}
