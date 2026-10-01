// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.FileSystems.Ext;

/// <summary>Everything an inode needs to point at a newly allocated file body.</summary>
internal sealed record ExtFileBlocks(byte[] IBlock, uint InodeFlags, long[] DataBlocks, long TotalBlocks);

internal static class ExtFileAllocator
{
    /// <summary>
    /// Allocates <paramref name="dataBlocks"/> data blocks plus whatever mapping blocks the file system's
    /// block map style needs, and writes the mapping blocks. The caller writes the data and the inode.
    /// </summary>
    public static ExtFileBlocks Allocate(ExtVolume volume, long dataBlocks)
    {
        return volume.UsesExtents ? AllocateExtents(volume, dataBlocks) : AllocateBlockMap(volume, dataBlocks);
    }

    /// <summary>Upper bound of the blocks <see cref="Allocate"/> takes; extent leaf blocks are not known before the data is placed.</summary>
    public static long EstimateBlocks(ExtVolume volume, long dataBlocks) =>
        dataBlocks + (volume.UsesExtents ? ExtExtentTree.InodeCapacity : ExtBlockMap.MappingBlockCount(dataBlocks, volume.BlockSize));

    private static ExtFileBlocks AllocateBlockMap(ExtVolume volume, long dataBlocks)
    {
        var blockSize = volume.BlockSize;
        if (dataBlocks > ExtBlockMap.MaxDataBlocks(blockSize))
        {
            throw new NotSupportedException("The file is too large for a block-mapped inode.");
        }

        var mapping = ExtBlockMap.MappingBlockCount(dataBlocks, blockSize);
        var allocated = volume.AllocateBlocks(dataBlocks + mapping);
        var iblock = ExtBlockMap.Build(allocated, dataBlocks, blockSize, (block, buffer) => volume.WriteBlock(block, buffer), out var data);
        return new ExtFileBlocks(iblock, 0, data, dataBlocks + mapping);
    }

    private static ExtFileBlocks AllocateExtents(ExtVolume volume, long dataBlocks)
    {
        var data = volume.AllocateBlocks(dataBlocks);
        var runs = ExtExtentTree.ToRuns(data);
        if (runs.Count <= ExtExtentTree.InodeCapacity)
        {
            return new ExtFileBlocks(ExtExtentTree.BuildInline(runs), ExtInode.FlagExtents, data, dataBlocks);
        }

        var perLeaf = ExtExtentTree.LeafCapacity(volume.BlockSize);
        var leafCount = (runs.Count + perLeaf - 1) / perLeaf;
        if (leafCount > ExtExtentTree.InodeCapacity)
        {
            throw new NotSupportedException("The file is too fragmented for a two-level extent tree.");
        }

        var leafBlocks = volume.AllocateBlocks(leafCount);
        var leaves = new List<(uint Logical, long Block)>(leafCount);
        for (var i = 0; i < leafCount; i++)
        {
            var chunk = runs.Skip(i * perLeaf).Take(perLeaf).ToList();
            volume.WriteBlock(leafBlocks[i], ExtExtentTree.BuildLeaf(chunk, volume.BlockSize));
            leaves.Add((chunk[0].Logical, leafBlocks[i]));
        }

        return new ExtFileBlocks(ExtExtentTree.BuildIndexRoot(leaves), ExtInode.FlagExtents, data, dataBlocks + leafCount);
    }
}
