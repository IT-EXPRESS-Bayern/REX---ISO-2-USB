// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace Bootrix.Core.FileSystems.Ext;

/// <summary>Creates the internal JBD2 journal in inode 8 with an empty, version 2 journal superblock.</summary>
internal static class ExtJournal
{
    public const long MinBlocks = 1024;

    private const uint Magic = 0xC03B3998;
    private const uint BlockTypeSuperblockV2 = 4;
    private const ushort ModeRegularOwnerOnly = ExtInode.ModeRegular | 0x180;
    private const byte BackupTypeInodeBlocks = 1;

    public static void Create(ExtVolume volume, long blocks, bool clearBody, CancellationToken cancellationToken)
    {
        var file = ExtFileAllocator.Allocate(volume, blocks);
        volume.WriteBlock(file.DataBlocks[0], BuildSuperblock(volume.BlockSize, blocks, volume.Superblock.Uuid));

        if (clearBody)
        {
            ClearBody(volume, file.DataBlocks, cancellationToken);
        }

        var now = volume.Now;
        var inode = new ExtInode
        {
            Mode = ModeRegularOwnerOnly,
            LinksCount = 1,
            Size = blocks * volume.BlockSize,
            Sectors = file.TotalBlocks * (volume.BlockSize / 512),
            Flags = file.InodeFlags,
            IBlock = file.IBlock,
            AccessTime = now,
            ChangeTime = now,
            ModificationTime = now,
            CreationTime = now,
            ExtraSize = volume.InodeExtraSize,
        };
        volume.WriteNewInode(ExtVolume.JournalInodeNumber, inode);

        // The superblock keeps a copy of the inode's block pointers so e2fsck can rebuild a damaged journal inode.
        var superblock = volume.Superblock;
        superblock.JournalInode = ExtVolume.JournalInodeNumber;
        superblock.JournalBackupType = BackupTypeInodeBlocks;
        var backup = superblock.JournalBlocks;
        backup.Clear();
        file.IBlock.CopyTo(backup);
        BinaryPrimitives.WriteUInt32LittleEndian(backup[(ExtInode.IBlockSize + 4)..], (uint)inode.Size);
    }

    public static byte[] BuildSuperblock(int blockSize, long blocks, ReadOnlySpan<byte> uuid)
    {
        var block = new byte[blockSize];
        BinaryPrimitives.WriteUInt32BigEndian(block, Magic);
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(0x04), BlockTypeSuperblockV2);
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(0x0C), (uint)blockSize);
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(0x10), (uint)blocks);
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(0x14), 1);
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(0x18), 1);
        uuid.CopyTo(block.AsSpan(0x30));
        BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(0x40), 1);
        return block;
    }

    // Walks the data blocks as runs so a contiguous journal is cleared with a few large writes.
    private static void ClearBody(ExtVolume volume, long[] dataBlocks, CancellationToken cancellationToken)
    {
        var index = 1;
        while (index < dataBlocks.Length)
        {
            var start = index;
            while (index + 1 < dataBlocks.Length && dataBlocks[index + 1] == dataBlocks[index] + 1)
            {
                index++;
            }

            index++;
            volume.Device.Zero(dataBlocks[start] * volume.BlockSize, (long)(index - start) * volume.BlockSize, cancellationToken);
        }
    }
}
