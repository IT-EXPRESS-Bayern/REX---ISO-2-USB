// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;

namespace Bootrix.Core.FileSystems.Ext;

internal sealed partial class ExtVolume
{
    public void EnsureCapacity(long blocks, int inodes)
    {
        if (Superblock.FreeBlocksCount < blocks)
        {
            throw NoSpace(blocks * BlockSize);
        }

        if (Superblock.FreeInodesCount < inodes)
        {
            throw NoSpace(Layout.InodeSize);
        }
    }

    /// <summary>Takes the first <paramref name="count"/> free blocks, scanning from group 0. The result is in allocation order.</summary>
    public long[] AllocateBlocks(long count)
    {
        if (count == 0)
        {
            return [];
        }

        if (count > Superblock.FreeBlocksCount)
        {
            throw NoSpace(count * BlockSize);
        }

        var blocks = new long[count];
        var found = 0;
        for (var group = 0; group < Groups.Length && found < count; group++)
        {
            var descriptor = Groups[group];
            if (descriptor.FreeBlocks == 0)
            {
                continue;
            }

            var bitmap = BlockBitmapOf(group);
            var limit = Layout.GroupBlockCount(group);
            var start = Layout.GroupStart(group);
            var taken = 0;
            var bit = 0;
            while (found < count && (bit = ExtBitmap.FindClear(bitmap, bit, limit)) >= 0)
            {
                ExtBitmap.Set(bitmap, bit);
                blocks[found++] = start + bit++;
                taken++;
            }

            if (taken == 0)
            {
                continue;
            }

            descriptor.FreeBlocks -= (ushort)taken;
            descriptor.Flags &= unchecked((ushort)~ExtGroupDescriptor.FlagBlockUninit);
            _blockBitmapsDirty[group] = true;
            Superblock.FreeBlocksCount -= (uint)taken;
        }

        if (found < count)
        {
            throw new InvalidDataException("The free block counters do not match the block bitmaps; run e2fsck.");
        }

        return blocks;
    }

    /// <summary>Allocates the lowest free inode number at or above s_first_ino.</summary>
    public uint AllocateInode(bool directory)
    {
        for (var group = 0; group < Groups.Length; group++)
        {
            if (Groups[group].FreeInodes == 0)
            {
                continue;
            }

            var first = group == 0 ? (int)Superblock.FirstInode - 1 : 0;
            var index = ExtBitmap.FindClear(InodeBitmapOf(group), first, Layout.InodesPerGroup);
            if (index < 0)
            {
                continue;
            }

            var number = (uint)(group * Layout.InodesPerGroup + index + 1);
            MarkInodeUsed(number, directory);
            return number;
        }

        throw NoSpace(Layout.InodeSize);
    }

    public void MarkInodeUsed(uint number, bool directory)
    {
        var group = (int)((number - 1) / (uint)Layout.InodesPerGroup);
        var index = (int)((number - 1) % (uint)Layout.InodesPerGroup);
        var descriptor = Groups[group];
        var bitmap = InodeBitmapOf(group);
        if (ExtBitmap.IsSet(bitmap, index))
        {
            return;
        }

        ExtBitmap.Set(bitmap, index);
        _inodeBitmapsDirty[group] = true;
        descriptor.FreeInodes--;
        Superblock.FreeInodesCount--;
        descriptor.Flags &= unchecked((ushort)~ExtGroupDescriptor.FlagInodeUninit);
        if (directory)
        {
            descriptor.UsedDirectories++;
        }

        if (HasGroupChecksums)
        {
            ExtendUsedInodeTable(descriptor, index);
        }
    }

    /// <summary>
    /// Inodes behind bg_itable_unused are never read by e2fsck or the kernel, so a lazily formatted table
    /// holds garbage there. Slots that move into the used range have to be cleared first.
    /// </summary>
    private void ExtendUsedInodeTable(ExtGroupDescriptor descriptor, int index)
    {
        var used = Layout.InodesPerGroup - descriptor.InodeTableUnused;
        if (index < used)
        {
            return;
        }

        if (!descriptor.HasFlag(ExtGroupDescriptor.FlagInodeTableZeroed))
        {
            var from = (long)descriptor.InodeTable * BlockSize + (long)used * Layout.InodeSize;
            Device.Zero(from, (long)(index - used + 1) * Layout.InodeSize);
        }

        descriptor.InodeTableUnused = (ushort)(Layout.InodesPerGroup - index - 1);
    }

    private byte[] BlockBitmapOf(int group)
    {
        if (_blockBitmaps[group] is { } cached)
        {
            return cached;
        }

        byte[] bitmap;
        if (Groups[group].HasFlag(ExtGroupDescriptor.FlagBlockUninit))
        {
            bitmap = SynthesizeBlockBitmap(group);
        }
        else
        {
            bitmap = new byte[BlockSize];
            ReadBlock(Groups[group].BlockBitmap, bitmap);
        }

        return _blockBitmaps[group] = bitmap;
    }

    private byte[] InodeBitmapOf(int group)
    {
        if (_inodeBitmaps[group] is { } cached)
        {
            return cached;
        }

        byte[] bitmap;
        if (Groups[group].HasFlag(ExtGroupDescriptor.FlagInodeUninit))
        {
            bitmap = SynthesizeInodeBitmap();
        }
        else
        {
            bitmap = new byte[BlockSize];
            ReadBlock(Groups[group].InodeBitmap, bitmap);
        }

        return _inodeBitmaps[group] = bitmap;
    }

    // A group flagged BLOCK_UNINIT has no bitmap on disk; it consists of its own metadata and nothing else.
    private byte[] SynthesizeBlockBitmap(int group)
    {
        var bitmap = new byte[BlockSize];
        var start = Layout.GroupStart(group);
        var count = Layout.GroupBlockCount(group);
        var descriptor = Groups[group];

        MarkInGroup(bitmap, start, count, start, Layout.SuperOverhead(group));
        MarkInGroup(bitmap, start, count, descriptor.BlockBitmap, 1);
        MarkInGroup(bitmap, start, count, descriptor.InodeBitmap, 1);
        MarkInGroup(bitmap, start, count, descriptor.InodeTable, Layout.InodeTableBlocks);
        ExtBitmap.SetRange(bitmap, count, BlockSize * 8 - count);
        return bitmap;
    }

    private byte[] SynthesizeInodeBitmap()
    {
        var bitmap = new byte[BlockSize];
        ExtBitmap.SetRange(bitmap, Layout.InodesPerGroup, BlockSize * 8 - Layout.InodesPerGroup);
        return bitmap;
    }

    private static void MarkInGroup(byte[] bitmap, long groupStart, int groupBlocks, long first, int length)
    {
        for (var i = 0; i < length; i++)
        {
            var relative = first + i - groupStart;
            if (relative >= 0 && relative < groupBlocks)
            {
                ExtBitmap.Set(bitmap, (int)relative);
            }
        }
    }

    private void WriteBitmaps()
    {
        for (var group = 0; group < Groups.Length; group++)
        {
            if (_blockBitmapsDirty[group] && _blockBitmaps[group] is { } blockBitmap)
            {
                WriteBlock(Groups[group].BlockBitmap, blockBitmap);
                _blockBitmapsDirty[group] = false;
            }

            if (_inodeBitmapsDirty[group] && _inodeBitmaps[group] is { } inodeBitmap)
            {
                WriteBlock(Groups[group].InodeBitmap, inodeBitmap);
                _inodeBitmapsDirty[group] = false;
            }
        }
    }

    private BootrixException NoSpace(long requiredBytes) =>
        new(ErrorCode.InsufficientSpace, "The ext file system has no room left.")
        {
            Arguments = [DescribeVolume(), FormatBytes(requiredBytes)],
        };

    private string DescribeVolume()
    {
        var name = Superblock.VolumeName;
        var length = name.IndexOf((byte)0);
        var label = System.Text.Encoding.UTF8.GetString(length < 0 ? name : name[..length]);
        return label.Length > 0 ? label : "ext";
    }

    internal static string FormatBytes(long bytes) =>
        string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{bytes:N0} B");
}
