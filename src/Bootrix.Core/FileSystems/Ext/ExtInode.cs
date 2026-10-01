// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace Bootrix.Core.FileSystems.Ext;

/// <summary>The fields of an on-disk inode this writer reads or sets.</summary>
internal sealed class ExtInode
{
    public const int BaseSize = 128;
    public const int IBlockSize = 60;
    public const int DefaultExtraSize = 32;
    public const ushort ModeDirectory = 0x4000;
    public const ushort ModeRegular = 0x8000;
    public const uint FlagIndexed = 0x1000;
    public const uint FlagExtents = 0x80000;

    public ushort Mode { get; set; }

    public uint Uid { get; set; }

    public uint Gid { get; set; }

    public long Size { get; set; }

    public long AccessTime { get; set; }

    public long ChangeTime { get; set; }

    public long ModificationTime { get; set; }

    public long CreationTime { get; set; }

    public ushort LinksCount { get; set; }

    /// <summary>Allocated space in 512-byte units, including mapping blocks.</summary>
    public long Sectors { get; set; }

    public uint Flags { get; set; }

    public byte[] IBlock { get; set; } = new byte[IBlockSize];

    /// <summary>Number of bytes used behind the original 128-byte structure; zero when the inode has none.</summary>
    public int ExtraSize { get; set; }

    public bool IsDirectory => (Mode & 0xF000) == ModeDirectory;

    public static ExtInode Read(ReadOnlySpan<byte> raw)
    {
        var extra = raw.Length > BaseSize ? BinaryPrimitives.ReadUInt16LittleEndian(raw[0x80..]) : 0;
        var inode = new ExtInode
        {
            Mode = BinaryPrimitives.ReadUInt16LittleEndian(raw),
            Uid = BinaryPrimitives.ReadUInt16LittleEndian(raw[0x02..]) | ((uint)BinaryPrimitives.ReadUInt16LittleEndian(raw[0x78..]) << 16),
            Gid = BinaryPrimitives.ReadUInt16LittleEndian(raw[0x18..]) | ((uint)BinaryPrimitives.ReadUInt16LittleEndian(raw[0x7A..]) << 16),
            Size = BinaryPrimitives.ReadUInt32LittleEndian(raw[0x04..]) | ((long)BinaryPrimitives.ReadUInt32LittleEndian(raw[0x6C..]) << 32),
            LinksCount = BinaryPrimitives.ReadUInt16LittleEndian(raw[0x1A..]),
            Sectors = BinaryPrimitives.ReadUInt32LittleEndian(raw[0x1C..]) | ((long)BinaryPrimitives.ReadUInt16LittleEndian(raw[0x74..]) << 32),
            Flags = BinaryPrimitives.ReadUInt32LittleEndian(raw[0x20..]),
            ExtraSize = extra,
        };

        inode.AccessTime = DecodeTime(raw, 0x08, 0x8C, extra);
        inode.ChangeTime = DecodeTime(raw, 0x0C, 0x84, extra);
        inode.ModificationTime = DecodeTime(raw, 0x10, 0x88, extra);
        inode.CreationTime = extra >= 0x94 - BaseSize ? DecodeTime(raw, 0x90, 0x94, extra) : 0;
        raw.Slice(0x28, IBlockSize).CopyTo(inode.IBlock);
        return inode;
    }

    /// <summary>Overlays the known fields on <paramref name="raw"/>; everything else in the slot is left alone.</summary>
    public void WriteTo(Span<byte> raw)
    {
        if (Sectors > uint.MaxValue)
        {
            throw new NotSupportedException("Files of 2 TiB and more need the huge_file layout, which is not implemented.");
        }

        BinaryPrimitives.WriteUInt16LittleEndian(raw, Mode);
        BinaryPrimitives.WriteUInt16LittleEndian(raw[0x02..], (ushort)Uid);
        BinaryPrimitives.WriteUInt32LittleEndian(raw[0x04..], (uint)Size);
        BinaryPrimitives.WriteUInt16LittleEndian(raw[0x18..], (ushort)Gid);
        BinaryPrimitives.WriteUInt16LittleEndian(raw[0x1A..], LinksCount);
        BinaryPrimitives.WriteUInt32LittleEndian(raw[0x1C..], (uint)Sectors);
        BinaryPrimitives.WriteUInt32LittleEndian(raw[0x20..], Flags);
        IBlock.CopyTo(raw[0x28..]);
        BinaryPrimitives.WriteUInt32LittleEndian(raw[0x6C..], (uint)(Size >> 32));
        BinaryPrimitives.WriteUInt16LittleEndian(raw[0x78..], (ushort)(Uid >> 16));
        BinaryPrimitives.WriteUInt16LittleEndian(raw[0x7A..], (ushort)(Gid >> 16));

        EncodeTime(raw, 0x08, 0x8C, AccessTime, ExtraSize);
        EncodeTime(raw, 0x0C, 0x84, ChangeTime, ExtraSize);
        EncodeTime(raw, 0x10, 0x88, ModificationTime, ExtraSize);

        if (raw.Length > BaseSize)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(raw[0x80..], (ushort)ExtraSize);
            if (ExtraSize >= 0x94 - BaseSize)
            {
                EncodeTime(raw, 0x90, 0x94, CreationTime, ExtraSize);
            }
        }
    }

    // The two low bits of the *_extra field extend the signed 32-bit seconds value (ext4 inode timestamps).
    private static long DecodeTime(ReadOnlySpan<byte> raw, int offset, int extraOffset, int extraSize)
    {
        long seconds = BinaryPrimitives.ReadInt32LittleEndian(raw[offset..]);
        if (extraOffset + 4 <= BaseSize + extraSize)
        {
            seconds += (long)(BinaryPrimitives.ReadUInt32LittleEndian(raw[extraOffset..]) & 3) << 32;
        }

        return seconds;
    }

    private static void EncodeTime(Span<byte> raw, int offset, int extraOffset, long seconds, int extraSize)
    {
        var low = unchecked((int)seconds);
        BinaryPrimitives.WriteInt32LittleEndian(raw[offset..], low);
        if (extraOffset + 4 <= BaseSize + extraSize)
        {
            var epoch = (uint)(((seconds - low) >> 32) & 3);
            BinaryPrimitives.WriteUInt32LittleEndian(raw[extraOffset..], epoch);
        }
    }
}
