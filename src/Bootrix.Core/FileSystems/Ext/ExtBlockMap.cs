// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace Bootrix.Core.FileSystems.Ext;

/// <summary>The classic ext2/ext3 mapping: 12 direct pointers plus single, double and triple indirect blocks.</summary>
internal static class ExtBlockMap
{
    private const int DirectBlocks = 12;
    private const int SingleIndirectSlot = 12;

    public static long MaxDataBlocks(int blockSize)
    {
        long pointers = blockSize / 4;
        return DirectBlocks + pointers + pointers * pointers + pointers * pointers * pointers;
    }

    /// <summary>Number of indirect blocks needed to map <paramref name="dataBlocks"/> data blocks.</summary>
    public static long MappingBlockCount(long dataBlocks, int blockSize)
    {
        var pointers = blockSize / 4;
        var remaining = dataBlocks - DirectBlocks;
        long count = 0;
        for (var level = 1; level <= 3 && remaining > 0; level++)
        {
            count += CountNode(level, ref remaining, pointers);
        }

        return count;
    }

    /// <summary>
    /// Builds i_block for a file whose blocks come in <paramref name="allocated"/> in this order: each
    /// indirect block is followed by the blocks it maps. Returns the data blocks in logical order.
    /// </summary>
    public static byte[] Build(long[] allocated, long dataBlocks, int blockSize, Action<long, byte[]> writeBlock, out long[] data)
    {
        var iblock = new byte[ExtInode.IBlockSize];
        var mapped = new long[dataBlocks];
        var position = 0;
        var dataIndex = 0;
        var remaining = dataBlocks - DirectBlocks;
        var pointers = blockSize / 4;

        for (var i = 0; i < DirectBlocks && i < dataBlocks; i++)
        {
            mapped[dataIndex++] = allocated[position];
            SetPointer(iblock, i, allocated[position++]);
        }

        for (var level = 1; level <= 3 && remaining > 0; level++)
        {
            SetPointer(iblock, SingleIndirectSlot + level - 1, BuildNode(level));
        }

        data = mapped;
        return iblock;

        long BuildNode(int level)
        {
            var self = allocated[position++];
            var buffer = new byte[blockSize];
            for (var slot = 0; slot < pointers && remaining > 0; slot++)
            {
                if (level == 1)
                {
                    mapped[dataIndex] = allocated[position++];
                    SetPointer(buffer, slot, mapped[dataIndex++]);
                    remaining--;
                }
                else
                {
                    SetPointer(buffer, slot, BuildNode(level - 1));
                }
            }

            writeBlock(self, buffer);
            return self;
        }
    }

    /// <summary>Resolves the first <paramref name="count"/> data blocks of an existing block-mapped file.</summary>
    public static List<long> Read(ReadOnlySpan<byte> iblock, long count, int blockSize, Action<long, byte[]> readBlock)
    {
        var blocks = new List<long>();
        var pointers = blockSize / 4;
        for (var i = 0; i < DirectBlocks && blocks.Count < count; i++)
        {
            blocks.Add(GetPointer(iblock, i));
        }

        for (var level = 1; level <= 3 && blocks.Count < count; level++)
        {
            Walk(GetPointer(iblock, SingleIndirectSlot + level - 1), level);
        }

        return blocks;

        void Walk(long block, int level)
        {
            if (block == 0)
            {
                throw new InvalidDataException("The file has a hole in its block map.");
            }

            var buffer = new byte[blockSize];
            readBlock(block, buffer);
            for (var slot = 0; slot < pointers && blocks.Count < count; slot++)
            {
                var target = GetPointer(buffer, slot);
                if (level == 1)
                {
                    blocks.Add(target);
                }
                else
                {
                    Walk(target, level - 1);
                }
            }
        }
    }

    private static long CountNode(int level, ref long remaining, int pointers)
    {
        long nodes = 1;
        for (var slot = 0; slot < pointers && remaining > 0; slot++)
        {
            if (level == 1)
            {
                remaining--;
            }
            else
            {
                nodes += CountNode(level - 1, ref remaining, pointers);
            }
        }

        return nodes;
    }

    private static void SetPointer(Span<byte> buffer, int slot, long block) =>
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[(slot * 4)..], checked((uint)block));

    private static long GetPointer(ReadOnlySpan<byte> buffer, int slot) =>
        BinaryPrimitives.ReadUInt32LittleEndian(buffer[(slot * 4)..]);
}
