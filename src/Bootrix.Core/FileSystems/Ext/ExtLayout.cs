// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.FileSystems.Ext;

/// <summary>
/// Block group geometry without flex_bg: every group holds its own bitmaps and inode table directly
/// behind the (optional) superblock and descriptor table copies.
/// </summary>
internal sealed class ExtLayout
{
    public const int MinBlockSize = 1024;
    public const int MaxBlockSize = 4096;

    // A trailing group with less data space than this is cut off instead of being formatted.
    private const int MinLastGroupDataBlocks = 50;
    private const int MinInodesPerGroup = 16;

    private ExtLayout(long blockCount, int blockSize, int inodesPerGroup, int inodeSize, int reservedGdtBlocks, bool sparseSuper)
    {
        BlockCount = blockCount;
        BlockSize = blockSize;
        InodesPerGroup = inodesPerGroup;
        InodeSize = inodeSize;
        ReservedGdtBlocks = reservedGdtBlocks;
        SparseSuper = sparseSuper;

        FirstDataBlock = blockSize == 1024 ? 1 : 0;
        BlocksPerGroup = blockSize * 8;
        GroupCount = CountGroups(blockCount, blockSize);
        GdtBlocks = (GroupCount * ExtGroupDescriptor.Size + blockSize - 1) / blockSize;
        InodeTableBlocks = (int)((long)inodesPerGroup * inodeSize / blockSize);
    }

    public long BlockCount { get; }

    public int BlockSize { get; }

    public int FirstDataBlock { get; }

    public int BlocksPerGroup { get; }

    public int GroupCount { get; }

    public int InodesPerGroup { get; }

    public int InodeSize { get; }

    public int GdtBlocks { get; }

    public int ReservedGdtBlocks { get; }

    public int InodeTableBlocks { get; }

    public bool SparseSuper { get; }

    public long TotalInodes => (long)InodesPerGroup * GroupCount;

    public static ExtLayout Plan(long sizeBytes, int blockSize, int bytesPerInode, int inodeSize)
    {
        var blocks = sizeBytes / blockSize;
        var inodesPerBlock = blockSize / inodeSize;
        var rounding = Math.Max(8, inodesPerBlock);

        while (true)
        {
            if (blocks > uint.MaxValue)
            {
                throw new NotSupportedException("File systems with 2^32 blocks or more need the 64bit feature, which is not implemented.");
            }

            var groups = CountGroups(blocks, blockSize);
            var wanted = Math.Max(blocks * blockSize / bytesPerInode, MinInodesPerGroup);
            var perGroup = (wanted + groups - 1) / groups;
            perGroup = (perGroup + rounding - 1) / rounding * rounding;
            perGroup = Math.Min(perGroup, blockSize * 8L);
            if (perGroup * groups > uint.MaxValue)
            {
                throw new NotSupportedException("The inode count exceeds 2^32; increase the bytes-per-inode ratio.");
            }

            var layout = new ExtLayout(blocks, blockSize, (int)perGroup, inodeSize, 0, true);
            var last = layout.GroupCount - 1;
            var lastBlocks = layout.GroupBlockCount(last);
            if (last > 0 && lastBlocks < layout.GroupOverhead(last) + MinLastGroupDataBlocks)
            {
                blocks -= lastBlocks;
                continue;
            }

            return layout;
        }
    }

    public static ExtLayout FromSuperblock(ExtSuperblock sb)
    {
        var blockSize = sb.BlockSize;
        var layout = new ExtLayout(
            sb.BlocksCount,
            blockSize,
            checked((int)sb.InodesPerGroup),
            sb.InodeSize,
            sb.ReservedGdtBlocks,
            sb.HasRoCompat(ExtFeatures.RoCompatSparseSuper));

        if (sb.BlocksPerGroup != layout.BlocksPerGroup || sb.FirstDataBlock != layout.FirstDataBlock)
        {
            throw new NotSupportedException("The file system uses a group size or first data block this writer does not support.");
        }

        return layout;
    }

    public long GroupStart(int group) => FirstDataBlock + (long)group * BlocksPerGroup;

    public int GroupBlockCount(int group) =>
        group == GroupCount - 1
            ? (int)(BlockCount - FirstDataBlock - (long)group * BlocksPerGroup)
            : BlocksPerGroup;

    public bool HasSuperBackup(int group) =>
        !SparseSuper || group <= 1 || IsPowerOf(group, 3) || IsPowerOf(group, 5) || IsPowerOf(group, 7);

    /// <summary>Blocks taken by the superblock, the descriptor table and the reserved descriptor blocks.</summary>
    public int SuperOverhead(int group) => HasSuperBackup(group) ? 1 + GdtBlocks + ReservedGdtBlocks : 0;

    public int GroupOverhead(int group) => SuperOverhead(group) + 2 + InodeTableBlocks;

    public long BlockBitmapLocation(int group) => GroupStart(group) + SuperOverhead(group);

    public long InodeBitmapLocation(int group) => BlockBitmapLocation(group) + 1;

    public long InodeTableLocation(int group) => BlockBitmapLocation(group) + 2;

    private static int CountGroups(long blocks, int blockSize)
    {
        var data = blocks - (blockSize == 1024 ? 1 : 0);
        var perGroup = blockSize * 8L;
        return Math.Max(1, (int)((data + perGroup - 1) / perGroup));
    }

    private static bool IsPowerOf(int value, int radix)
    {
        while (value % radix == 0)
        {
            value /= radix;
        }

        return value == 1;
    }
}
