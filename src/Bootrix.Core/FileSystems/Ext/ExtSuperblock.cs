// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace Bootrix.Core.FileSystems.Ext;

/// <summary>
/// Typed view over the 1024-byte on-disk superblock. The raw bytes are kept so that fields
/// this writer does not know about survive a read-modify-write cycle.
/// </summary>
internal sealed class ExtSuperblock
{
    public const int Size = 1024;
    public const int DiskOffset = 1024;
    public const ushort ValidMagic = 0xEF53;
    public const ushort StateClean = 1;

    private readonly byte[] _data;

    public ExtSuperblock()
    {
        _data = new byte[Size];
    }

    public ExtSuperblock(ReadOnlySpan<byte> raw)
    {
        _data = raw[..Size].ToArray();
    }

    public ReadOnlySpan<byte> Bytes => _data;

    public ExtSuperblock Clone() => new(_data);

    public uint InodesCount { get => U32(0x00); set => SetU32(0x00, value); }

    public uint BlocksCount { get => U32(0x04); set => SetU32(0x04, value); }

    public uint ReservedBlocksCount { get => U32(0x08); set => SetU32(0x08, value); }

    public uint FreeBlocksCount { get => U32(0x0C); set => SetU32(0x0C, value); }

    public uint FreeInodesCount { get => U32(0x10); set => SetU32(0x10, value); }

    public uint FirstDataBlock { get => U32(0x14); set => SetU32(0x14, value); }

    public uint LogBlockSize { get => U32(0x18); set => SetU32(0x18, value); }

    /// <summary>Without bigalloc the cluster size has to equal the block size.</summary>
    public uint LogClusterSize { get => U32(0x1C); set => SetU32(0x1C, value); }

    public uint BlocksPerGroup { get => U32(0x20); set => SetU32(0x20, value); }

    public uint ClustersPerGroup { get => U32(0x24); set => SetU32(0x24, value); }

    public uint InodesPerGroup { get => U32(0x28); set => SetU32(0x28, value); }

    public uint WriteTime { get => U32(0x30); set => SetU32(0x30, value); }

    public ushort MaxMountCount { get => U16(0x36); set => SetU16(0x36, value); }

    public ushort Magic { get => U16(0x38); set => SetU16(0x38, value); }

    public ushort State { get => U16(0x3A); set => SetU16(0x3A, value); }

    public ushort Errors { get => U16(0x3C); set => SetU16(0x3C, value); }

    public uint LastCheck { get => U32(0x40); set => SetU32(0x40, value); }

    public uint RevLevel { get => U32(0x4C); set => SetU32(0x4C, value); }

    public uint FirstInode { get => U32(0x54); set => SetU32(0x54, value); }

    public ushort InodeSize { get => U16(0x58); set => SetU16(0x58, value); }

    public ushort BlockGroupNumber { get => U16(0x5A); set => SetU16(0x5A, value); }

    public uint FeatureCompat { get => U32(0x5C); set => SetU32(0x5C, value); }

    public uint FeatureIncompat { get => U32(0x60); set => SetU32(0x60, value); }

    public uint FeatureRoCompat { get => U32(0x64); set => SetU32(0x64, value); }

    public Span<byte> Uuid => _data.AsSpan(0x68, 16);

    public Span<byte> VolumeName => _data.AsSpan(0x78, 16);

    public ushort ReservedGdtBlocks { get => U16(0xCE); set => SetU16(0xCE, value); }

    public uint JournalInode { get => U32(0xE0); set => SetU32(0xE0, value); }

    public Span<byte> HashSeed => _data.AsSpan(0xEC, 16);

    public byte DefaultHashVersion { get => _data[0xFC]; set => _data[0xFC] = value; }

    public byte JournalBackupType { get => _data[0xFD]; set => _data[0xFD] = value; }

    public uint DefaultMountOptions { get => U32(0x100); set => SetU32(0x100, value); }

    public uint MkfsTime { get => U32(0x108); set => SetU32(0x108, value); }

    /// <summary>Copy of the journal inode's i_block[15] followed by i_size_high and i_size.</summary>
    public Span<byte> JournalBlocks => _data.AsSpan(0x10C, 68);

    public ushort MinExtraIsize { get => U16(0x15C); set => SetU16(0x15C, value); }

    public ushort WantExtraIsize { get => U16(0x15E); set => SetU16(0x15E, value); }

    public uint Flags { get => U32(0x160); set => SetU32(0x160, value); }

    public int BlockSize => 1024 << (int)LogBlockSize;

    public bool HasIncompat(uint flag) => (FeatureIncompat & flag) != 0;

    public bool HasRoCompat(uint flag) => (FeatureRoCompat & flag) != 0;

    private uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(offset));

    private ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(offset));

    private void SetU32(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(_data.AsSpan(offset), value);

    private void SetU16(int offset, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(_data.AsSpan(offset), value);
}
