// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace Bootrix.Core.FileSystems.Ext;

internal readonly record struct ExtentRun(uint Logical, long Start, int Length);

/// <summary>Extent trees of depth 0 (inside i_block) and depth 1 (one level of leaf blocks), without checksums.</summary>
internal static class ExtExtentTree
{
    public const int InodeCapacity = 4;

    // Longer runs would need the uninitialized-extent encoding; initialized extents stop at 2^15 blocks.
    private const int MaxRunLength = 32768;
    private const ushort Magic = 0xF30A;
    private const int HeaderSize = 12;
    private const int EntrySize = 12;

    public static int LeafCapacity(int blockSize) => (blockSize - HeaderSize) / EntrySize;

    public static List<ExtentRun> ToRuns(ReadOnlySpan<long> blocks)
    {
        var runs = new List<ExtentRun>();
        for (var i = 0; i < blocks.Length; i++)
        {
            if (runs.Count > 0)
            {
                var last = runs[^1];
                if (last.Start + last.Length == blocks[i] && last.Length < MaxRunLength)
                {
                    runs[^1] = last with { Length = last.Length + 1 };
                    continue;
                }
            }

            runs.Add(new ExtentRun((uint)i, blocks[i], 1));
        }

        return runs;
    }

    /// <summary>Depth-0 tree for at most <see cref="InodeCapacity"/> runs; an empty list gives a valid empty tree.</summary>
    public static byte[] BuildInline(IReadOnlyList<ExtentRun> runs)
    {
        var iblock = new byte[ExtInode.IBlockSize];
        WriteHeader(iblock, runs.Count, InodeCapacity, 0);
        WriteRuns(iblock, runs);
        return iblock;
    }

    public static byte[] BuildLeaf(IReadOnlyList<ExtentRun> runs, int blockSize)
    {
        var block = new byte[blockSize];
        WriteHeader(block, runs.Count, LeafCapacity(blockSize), 0);
        WriteRuns(block, runs);
        return block;
    }

    /// <summary>Depth-1 root inside i_block pointing at leaf blocks; each leaf is identified by its first logical block.</summary>
    public static byte[] BuildIndexRoot(IReadOnlyList<(uint Logical, long Block)> leaves)
    {
        var iblock = new byte[ExtInode.IBlockSize];
        WriteHeader(iblock, leaves.Count, InodeCapacity, 1);
        for (var i = 0; i < leaves.Count; i++)
        {
            var entry = iblock.AsSpan(HeaderSize + i * EntrySize);
            BinaryPrimitives.WriteUInt32LittleEndian(entry, leaves[i].Logical);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], (uint)leaves[i].Block);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[8..], (ushort)(leaves[i].Block >> 32));
        }

        return iblock;
    }

    /// <summary>Resolves the first <paramref name="count"/> data blocks of an extent-mapped file.</summary>
    public static List<long> Read(ReadOnlySpan<byte> iblock, long count, int blockSize, Action<long, byte[]> readBlock)
    {
        var blocks = new List<long>();
        ReadNode(iblock, blocks, count, blockSize, readBlock);
        return blocks;
    }

    private static void ReadNode(ReadOnlySpan<byte> node, List<long> blocks, long count, int blockSize, Action<long, byte[]> readBlock)
    {
        if (BinaryPrimitives.ReadUInt16LittleEndian(node) != Magic)
        {
            throw new InvalidDataException("The extent tree header is invalid.");
        }

        var entries = BinaryPrimitives.ReadUInt16LittleEndian(node[2..]);
        var depth = BinaryPrimitives.ReadUInt16LittleEndian(node[6..]);
        for (var i = 0; i < entries && blocks.Count < count; i++)
        {
            var entry = node[(HeaderSize + i * EntrySize)..];
            if (depth == 0)
            {
                var length = BinaryPrimitives.ReadUInt16LittleEndian(entry[4..]);
                length = (ushort)(length > MaxRunLength ? length - MaxRunLength : length);
                var start = BinaryPrimitives.ReadUInt32LittleEndian(entry[8..])
                    | ((long)BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]) << 32);
                for (var k = 0; k < length && blocks.Count < count; k++)
                {
                    blocks.Add(start + k);
                }
            }
            else
            {
                var leaf = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..])
                    | ((long)BinaryPrimitives.ReadUInt16LittleEndian(entry[8..]) << 32);
                var buffer = new byte[blockSize];
                readBlock(leaf, buffer);
                ReadNode(buffer, blocks, count, blockSize, readBlock);
            }
        }
    }

    private static void WriteHeader(Span<byte> node, int entries, int capacity, int depth)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(node, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(node[2..], (ushort)entries);
        BinaryPrimitives.WriteUInt16LittleEndian(node[4..], (ushort)capacity);
        BinaryPrimitives.WriteUInt16LittleEndian(node[6..], (ushort)depth);
    }

    private static void WriteRuns(Span<byte> node, IReadOnlyList<ExtentRun> runs)
    {
        for (var i = 0; i < runs.Count; i++)
        {
            var entry = node[(HeaderSize + i * EntrySize)..];
            BinaryPrimitives.WriteUInt32LittleEndian(entry, runs[i].Logical);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], (ushort)runs[i].Length);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[6..], (ushort)(runs[i].Start >> 32));
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)runs[i].Start);
        }
    }
}
