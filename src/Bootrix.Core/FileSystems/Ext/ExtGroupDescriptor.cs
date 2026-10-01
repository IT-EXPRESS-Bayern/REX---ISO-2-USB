// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace Bootrix.Core.FileSystems.Ext;

/// <summary>The 32-byte group descriptor used when the 64bit feature is off.</summary>
internal sealed class ExtGroupDescriptor
{
    public const int Size = 32;
    public const ushort FlagInodeUninit = 0x1;
    public const ushort FlagBlockUninit = 0x2;
    public const ushort FlagInodeTableZeroed = 0x4;

    private const int ChecksumOffset = 0x1E;

    public uint BlockBitmap { get; set; }

    public uint InodeBitmap { get; set; }

    public uint InodeTable { get; set; }

    public ushort FreeBlocks { get; set; }

    public ushort FreeInodes { get; set; }

    public ushort UsedDirectories { get; set; }

    public ushort Flags { get; set; }

    public ushort InodeTableUnused { get; set; }

    public ushort Checksum { get; set; }

    public bool HasFlag(ushort flag) => (Flags & flag) != 0;

    public static ExtGroupDescriptor Read(ReadOnlySpan<byte> raw) => new()
    {
        BlockBitmap = BinaryPrimitives.ReadUInt32LittleEndian(raw[0x00..]),
        InodeBitmap = BinaryPrimitives.ReadUInt32LittleEndian(raw[0x04..]),
        InodeTable = BinaryPrimitives.ReadUInt32LittleEndian(raw[0x08..]),
        FreeBlocks = BinaryPrimitives.ReadUInt16LittleEndian(raw[0x0C..]),
        FreeInodes = BinaryPrimitives.ReadUInt16LittleEndian(raw[0x0E..]),
        UsedDirectories = BinaryPrimitives.ReadUInt16LittleEndian(raw[0x10..]),
        Flags = BinaryPrimitives.ReadUInt16LittleEndian(raw[0x12..]),
        InodeTableUnused = BinaryPrimitives.ReadUInt16LittleEndian(raw[0x1C..]),
        Checksum = BinaryPrimitives.ReadUInt16LittleEndian(raw[ChecksumOffset..]),
    };

    public void Write(Span<byte> raw)
    {
        raw[..Size].Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(raw[0x00..], BlockBitmap);
        BinaryPrimitives.WriteUInt32LittleEndian(raw[0x04..], InodeBitmap);
        BinaryPrimitives.WriteUInt32LittleEndian(raw[0x08..], InodeTable);
        BinaryPrimitives.WriteUInt16LittleEndian(raw[0x0C..], FreeBlocks);
        BinaryPrimitives.WriteUInt16LittleEndian(raw[0x0E..], FreeInodes);
        BinaryPrimitives.WriteUInt16LittleEndian(raw[0x10..], UsedDirectories);
        BinaryPrimitives.WriteUInt16LittleEndian(raw[0x12..], Flags);
        BinaryPrimitives.WriteUInt16LittleEndian(raw[0x1C..], InodeTableUnused);
        BinaryPrimitives.WriteUInt16LittleEndian(raw[ChecksumOffset..], Checksum);
    }

    /// <summary>crc16 over the file system UUID, the group number and the descriptor without its checksum field.</summary>
    public ushort ComputeChecksum(ReadOnlySpan<byte> uuid, int group)
    {
        Span<byte> raw = stackalloc byte[Size];
        Write(raw);
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(number, (uint)group);

        var crc = Crc16.Update(0xFFFF, uuid);
        crc = Crc16.Update(crc, number);
        return Crc16.Update(crc, raw[..ChecksumOffset]);
    }
}
