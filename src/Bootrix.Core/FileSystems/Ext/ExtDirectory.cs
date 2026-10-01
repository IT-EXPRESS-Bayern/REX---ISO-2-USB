// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace Bootrix.Core.FileSystems.Ext;

internal readonly record struct ExtDirectoryEntry(uint Inode, byte FileType, string Name);

/// <summary>Linear directory blocks with file type bytes (ext4_dir_entry_2), without htree indexes or checksums.</summary>
internal static class ExtDirectory
{
    public const byte TypeRegular = 1;
    public const byte TypeDirectory = 2;
    public const int MaxNameLength = 255;

    private const int EntryHeaderSize = 8;

    public static int RecordLength(int nameLength) => (EntryHeaderSize + nameLength + 3) & ~3;

    /// <summary>A block with no entries: one unused record that spans the whole block.</summary>
    public static byte[] EmptyBlock(int blockSize) => Build(blockSize, []);

    /// <summary>Packs the entries one after another; the last one stretches to the end of the block.</summary>
    public static byte[] Build(int blockSize, IReadOnlyList<ExtDirectoryEntry> entries)
    {
        var block = new byte[blockSize];
        if (entries.Count == 0)
        {
            WriteHeader(block, 0, 0, blockSize, 0, 0);
            return block;
        }

        var offset = 0;
        for (var i = 0; i < entries.Count; i++)
        {
            var name = System.Text.Encoding.UTF8.GetBytes(entries[i].Name);
            var length = i == entries.Count - 1 ? blockSize - offset : RecordLength(name.Length);
            if (offset + RecordLength(name.Length) > blockSize)
            {
                throw new ArgumentException("The entries do not fit into one directory block.", nameof(entries));
            }

            WriteHeader(block, offset, entries[i].Inode, length, name.Length, entries[i].FileType);
            name.CopyTo(block.AsSpan(offset + EntryHeaderSize));
            offset += length;
        }

        return block;
    }

    public static bool Contains(ReadOnlySpan<byte> block, ReadOnlySpan<byte> name)
    {
        var offset = 0;
        while (offset < block.Length)
        {
            var (inode, length, nameLength) = ReadHeader(block, offset);
            if (inode != 0 && block.Slice(offset + EntryHeaderSize, nameLength).SequenceEqual(name))
            {
                return true;
            }

            offset += length;
        }

        return false;
    }

    public static bool HasRoomFor(ReadOnlySpan<byte> block, int nameLength) => FindSlot(block, nameLength).Offset >= 0;

    /// <summary>Inserts the entry into slack space of an existing record, or into an unused one.</summary>
    public static bool TryAdd(Span<byte> block, ReadOnlySpan<byte> name, uint inode, byte fileType)
    {
        var (offset, used, length) = FindSlot(block, name.Length);
        if (offset < 0)
        {
            return false;
        }

        var needed = RecordLength(name.Length);
        var entryOffset = offset + used;
        if (used > 0)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(block[(offset + 4)..], (ushort)used);
        }

        WriteHeader(block, entryOffset, inode, length - used, name.Length, fileType);
        name.CopyTo(block[(entryOffset + EntryHeaderSize)..]);
        block[(entryOffset + EntryHeaderSize + name.Length)..(entryOffset + needed)].Clear();
        return true;
    }

    // Offset of a record with enough slack, the bytes of it that are in use, and its full length.
    private static (int Offset, int Used, int Length) FindSlot(ReadOnlySpan<byte> block, int nameLength)
    {
        var needed = RecordLength(nameLength);
        var offset = 0;
        while (offset < block.Length)
        {
            var (inode, length, existingNameLength) = ReadHeader(block, offset);
            var used = inode == 0 ? 0 : RecordLength(existingNameLength);
            if (length - used >= needed)
            {
                return (offset, used, length);
            }

            offset += length;
        }

        return (-1, 0, 0);
    }

    private static (uint Inode, int Length, int NameLength) ReadHeader(ReadOnlySpan<byte> block, int offset)
    {
        var inode = BinaryPrimitives.ReadUInt32LittleEndian(block[offset..]);
        int length = BinaryPrimitives.ReadUInt16LittleEndian(block[(offset + 4)..]);
        int nameLength = block[offset + 6];
        if (length < EntryHeaderSize || (length & 3) != 0 || offset + length > block.Length || RecordLength(nameLength) > length)
        {
            throw new InvalidDataException("A directory block contains a damaged entry; run e2fsck.");
        }

        return (inode, length, nameLength);
    }

    private static void WriteHeader(Span<byte> block, int offset, uint inode, int recordLength, int nameLength, byte fileType)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(block[offset..], inode);
        BinaryPrimitives.WriteUInt16LittleEndian(block[(offset + 4)..], (ushort)recordLength);
        block[offset + 6] = (byte)nameLength;
        block[offset + 7] = fileType;
    }
}
