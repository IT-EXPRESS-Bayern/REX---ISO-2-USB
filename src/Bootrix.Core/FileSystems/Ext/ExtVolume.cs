// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.FileSystems.Ext;

/// <summary>
/// In-memory state of one ext file system: superblock, group descriptors and the bitmaps that have
/// been touched. Inode and data writes go straight to the stream; <see cref="Flush"/> persists the
/// bookkeeping (bitmaps, descriptor table, superblock and their backup copies).
/// </summary>
internal sealed partial class ExtVolume
{
    public const uint RootInode = 2;
    public const uint JournalInodeNumber = 8;

    private readonly byte[]?[] _blockBitmaps;
    private readonly byte[]?[] _inodeBitmaps;
    private readonly bool[] _blockBitmapsDirty;
    private readonly bool[] _inodeBitmapsDirty;

    private ExtVolume(ExtDevice device, ExtSuperblock superblock, ExtLayout layout, ExtGroupDescriptor[] groups, TimeProvider clock)
    {
        Device = device;
        Superblock = superblock;
        Layout = layout;
        Groups = groups;
        Clock = clock;
        _blockBitmaps = new byte[]?[layout.GroupCount];
        _inodeBitmaps = new byte[]?[layout.GroupCount];
        _blockBitmapsDirty = new bool[layout.GroupCount];
        _inodeBitmapsDirty = new bool[layout.GroupCount];
    }

    public ExtDevice Device { get; }

    public ExtSuperblock Superblock { get; }

    public ExtLayout Layout { get; }

    public ExtGroupDescriptor[] Groups { get; }

    public TimeProvider Clock { get; }

    public int BlockSize => Layout.BlockSize;

    public bool HasGroupChecksums => Superblock.HasRoCompat(ExtFeatures.RoCompatGroupChecksums);

    public bool UsesExtents => Superblock.HasIncompat(ExtFeatures.IncompatExtents);

    public long Now => Clock.GetUtcNow().ToUnixTimeSeconds();

    public int ReservedInodes => (int)Superblock.FirstInode - 1;

    public int InodeExtraSize => Layout.InodeSize > ExtInode.BaseSize ? ExtInode.DefaultExtraSize : 0;

    /// <summary>Wraps a freshly planned file system whose metadata is not on the stream yet.</summary>
    public static ExtVolume Create(ExtDevice device, ExtSuperblock superblock, ExtLayout layout, ExtGroupDescriptor[] groups, TimeProvider clock)
    {
        var volume = new ExtVolume(device, superblock, layout, groups, clock);
        for (var group = 0; group < groups.Length; group++)
        {
            if (!groups[group].HasFlag(ExtGroupDescriptor.FlagBlockUninit))
            {
                volume._blockBitmaps[group] = volume.SynthesizeBlockBitmap(group);
                volume._blockBitmapsDirty[group] = true;
            }

            if (!groups[group].HasFlag(ExtGroupDescriptor.FlagInodeUninit))
            {
                volume._inodeBitmaps[group] = volume.SynthesizeInodeBitmap();
                volume._inodeBitmapsDirty[group] = true;
            }
        }

        return volume;
    }

    public static ExtVolume Open(Stream stream, TimeProvider clock)
    {
        var device = new ExtDevice(stream);
        var raw = new byte[ExtSuperblock.Size];
        device.Read(ExtSuperblock.DiskOffset, raw);
        var superblock = new ExtSuperblock(raw);
        Validate(superblock);

        var layout = ExtLayout.FromSuperblock(superblock);
        var table = new byte[layout.GdtBlocks * layout.BlockSize];
        device.Read((layout.FirstDataBlock + 1L) * layout.BlockSize, table);
        var groups = new ExtGroupDescriptor[layout.GroupCount];
        for (var group = 0; group < groups.Length; group++)
        {
            groups[group] = ExtGroupDescriptor.Read(table.AsSpan(group * ExtGroupDescriptor.Size));
        }

        return new ExtVolume(device, superblock, layout, groups, clock);
    }

    public void ReadBlock(long block, Span<byte> buffer) => Device.Read(block * BlockSize, buffer);

    public void WriteBlock(long block, ReadOnlySpan<byte> data) => Device.Write(block * BlockSize, data);

    public ExtInode ReadInode(uint number)
    {
        var raw = new byte[Layout.InodeSize];
        Device.Read(InodeOffset(number), raw);
        return ExtInode.Read(raw);
    }

    /// <summary>Overlays the inode's fields on the existing slot, keeping extended attributes and unknown fields.</summary>
    public void WriteInode(uint number, ExtInode inode)
    {
        var raw = new byte[Layout.InodeSize];
        Device.Read(InodeOffset(number), raw);
        inode.WriteTo(raw);
        Device.Write(InodeOffset(number), raw);
    }

    public void WriteNewInode(uint number, ExtInode inode)
    {
        var raw = new byte[Layout.InodeSize];
        inode.WriteTo(raw);
        Device.Write(InodeOffset(number), raw);
    }

    public void Flush()
    {
        Superblock.WriteTime = (uint)Now;
        WriteBitmaps();

        var table = new byte[Layout.GdtBlocks * BlockSize];
        for (var group = 0; group < Groups.Length; group++)
        {
            if (HasGroupChecksums)
            {
                Groups[group].Checksum = Groups[group].ComputeChecksum(Superblock.Uuid, group);
            }

            Groups[group].Write(table.AsSpan(group * ExtGroupDescriptor.Size));
        }

        var backup = Superblock.Clone();
        backup.State = 0;
        var block = new byte[BlockSize];
        for (var group = 1; group < Layout.GroupCount; group++)
        {
            if (!Layout.HasSuperBackup(group))
            {
                continue;
            }

            backup.BlockGroupNumber = (ushort)group;
            block.AsSpan().Clear();
            backup.Bytes.CopyTo(block);
            WriteBlock(Layout.GroupStart(group), block);
            Device.Write((Layout.GroupStart(group) + 1) * BlockSize, table);
        }

        Superblock.BlockGroupNumber = 0;
        Device.Write((Layout.FirstDataBlock + 1L) * BlockSize, table);
        Device.Write(ExtSuperblock.DiskOffset, Superblock.Bytes);
        Device.Flush();
    }

    private long InodeOffset(uint number)
    {
        var index = number - 1;
        var group = (int)(index / (uint)Layout.InodesPerGroup);
        if (number == 0 || group >= Groups.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(number), number, "Inode number is outside the file system.");
        }

        return (long)Groups[group].InodeTable * BlockSize + (long)(index % (uint)Layout.InodesPerGroup) * Layout.InodeSize;
    }

    private static void Validate(ExtSuperblock sb)
    {
        if (sb.Magic != ExtSuperblock.ValidMagic)
        {
            throw new InvalidDataException("The stream does not contain an ext2/3/4 file system.");
        }

        if (sb.RevLevel != 1 || sb.BlockSize is < ExtLayout.MinBlockSize or > ExtLayout.MaxBlockSize
            || sb.InodeSize < ExtInode.BaseSize || sb.InodeSize > sb.BlockSize || sb.FirstInode < 11)
        {
            throw new NotSupportedException("The file system uses a superblock revision or geometry this writer does not support.");
        }

        if (sb.HasIncompat(ExtFeatures.IncompatRecover))
        {
            throw new InvalidOperationException("The file system journal needs recovery; run e2fsck before modifying it.");
        }

        var compat = sb.FeatureCompat & ~ExtFeatures.SupportedCompat;
        var incompat = sb.FeatureIncompat & ~ExtFeatures.SupportedIncompat;
        var roCompat = sb.FeatureRoCompat & ~ExtFeatures.SupportedRoCompat;
        if ((compat | incompat | roCompat) != 0)
        {
            throw new NotSupportedException(
                $"The file system uses features this writer does not support (compat 0x{compat:X}, incompat 0x{incompat:X}, ro_compat 0x{roCompat:X}).");
        }
    }
}
