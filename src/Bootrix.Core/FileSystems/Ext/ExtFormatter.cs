// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.FileSystems.Ext;

/// <summary>
/// Creates ext2, ext3 and ext4-lite file systems in a stream, writing only metadata.
/// The layout follows the kernel's ext4 disk layout documentation: no flex_bg, no 64bit,
/// no metadata checksums, one superblock and descriptor table copy per sparse_super group.
/// The stream starts at the first byte of the file system and has to allow positioned reads and writes of
/// any alignment; a raw device needs a buffering wrapper.
/// </summary>
public static class ExtFormatter
{
    private const uint FirstNonReservedInode = 11;
    private const ushort MaxMountCountDisabled = 0xFFFF;
    private const ushort ErrorsContinue = 1;
    private const uint DefaultMountUserXattrAcl = 0x4 | 0x8;
    private const uint SignedDirectoryHash = 0x1;
    private const byte HashHalfMd4 = 1;

    public static ExtFormatResult Format(Stream target, ExtFormatOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);

        var device = new ExtDevice(target);
        var plan = ExtFormatPlan.Create(options, options.SizeBytes ?? device.Length);
        var layout = plan.Layout;
        var clock = options.TimeProvider ?? TimeProvider.System;

        device.EnsureLength(layout.BlockCount * layout.BlockSize);
        if (!plan.Lazy && !options.AssumeZeroedTarget)
        {
            ZeroInodeTables(device, layout, cancellationToken);
        }

        var superblock = BuildSuperblock(plan, (uint)clock.GetUtcNow().ToUnixTimeSeconds());
        var volume = ExtVolume.Create(device, superblock, layout, BuildDescriptors(plan, superblock), clock);

        ExtRootDirectory.Create(volume);
        if (plan.JournalBlocks > 0)
        {
            ExtJournal.Create(volume, plan.JournalBlocks, clearBody: !plan.Lazy && !options.AssumeZeroedTarget, cancellationToken);
        }

        foreach (var file in options.RootFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExtRootDirectory.AddFile(volume, file);
        }

        volume.Flush();
        return new ExtFormatResult(
            options.Type,
            new Guid(plan.Uuid, bigEndian: true),
            layout.BlockSize,
            layout.BlockCount,
            layout.GroupCount,
            layout.InodesPerGroup,
            plan.JournalBlocks);
    }

    private static ExtSuperblock BuildSuperblock(ExtFormatPlan plan, uint now)
    {
        var layout = plan.Layout;
        var logBlockSize = (uint)Math.Log2(layout.BlockSize / 1024);
        var superblock = new ExtSuperblock
        {
            InodesCount = (uint)layout.TotalInodes,
            BlocksCount = (uint)layout.BlockCount,
            ReservedBlocksCount = (uint)(layout.BlockCount * plan.Options.ReservedBlocksPercent / 100),
            FreeInodesCount = (uint)layout.TotalInodes,
            FirstDataBlock = (uint)layout.FirstDataBlock,
            LogBlockSize = logBlockSize,
            LogClusterSize = logBlockSize,
            BlocksPerGroup = (uint)layout.BlocksPerGroup,
            ClustersPerGroup = (uint)layout.BlocksPerGroup,
            InodesPerGroup = (uint)layout.InodesPerGroup,
            WriteTime = now,
            MaxMountCount = MaxMountCountDisabled,
            Magic = ExtSuperblock.ValidMagic,
            State = ExtSuperblock.StateClean,
            Errors = ErrorsContinue,
            LastCheck = now,
            RevLevel = 1,
            FirstInode = FirstNonReservedInode,
            InodeSize = (ushort)layout.InodeSize,
            FeatureCompat = plan.Compat,
            FeatureIncompat = plan.Incompat,
            FeatureRoCompat = plan.RoCompat,
            DefaultHashVersion = HashHalfMd4,
            DefaultMountOptions = DefaultMountUserXattrAcl,
            MkfsTime = now,
            Flags = SignedDirectoryHash,
        };

        plan.Uuid.CopyTo(superblock.Uuid);
        plan.Label.CopyTo(superblock.VolumeName);
        plan.HashSeed.CopyTo(superblock.HashSeed);
        if (layout.InodeSize > ExtInode.BaseSize)
        {
            superblock.MinExtraIsize = ExtInode.DefaultExtraSize;
            superblock.WantExtraIsize = ExtInode.DefaultExtraSize;
        }

        long free = 0;
        for (var group = 0; group < layout.GroupCount; group++)
        {
            free += layout.GroupBlockCount(group) - layout.GroupOverhead(group);
        }

        superblock.FreeBlocksCount = (uint)free;
        return superblock;
    }

    private static ExtGroupDescriptor[] BuildDescriptors(ExtFormatPlan plan, ExtSuperblock superblock)
    {
        var layout = plan.Layout;
        var descriptors = new ExtGroupDescriptor[layout.GroupCount];
        for (var group = 0; group < descriptors.Length; group++)
        {
            ushort flags = 0;
            if (plan.Lazy)
            {
                if (plan.Options.AssumeZeroedTarget)
                {
                    flags |= ExtGroupDescriptor.FlagInodeTableZeroed;
                }

                // Group 0 holds the root and the journal. The last group is shorter than a bitmap, and its
                // padding bits have to be on disk, so it stays initialized as well.
                if (group > 0)
                {
                    flags |= ExtGroupDescriptor.FlagInodeUninit;
                    if (group < layout.GroupCount - 1)
                    {
                        flags |= ExtGroupDescriptor.FlagBlockUninit;
                    }
                }
            }

            descriptors[group] = new ExtGroupDescriptor
            {
                BlockBitmap = (uint)layout.BlockBitmapLocation(group),
                InodeBitmap = (uint)layout.InodeBitmapLocation(group),
                InodeTable = (uint)layout.InodeTableLocation(group),
                FreeBlocks = (ushort)(layout.GroupBlockCount(group) - layout.GroupOverhead(group)),
                FreeInodes = (ushort)layout.InodesPerGroup,
                InodeTableUnused = plan.Lazy ? (ushort)layout.InodesPerGroup : (ushort)0,
                Flags = flags,
            };
        }

        return descriptors;
    }

    private static void ZeroInodeTables(ExtDevice device, ExtLayout layout, CancellationToken cancellationToken)
    {
        var length = (long)layout.InodeTableBlocks * layout.BlockSize;
        for (var group = 0; group < layout.GroupCount; group++)
        {
            device.Zero(layout.InodeTableLocation(group) * layout.BlockSize, length, cancellationToken);
        }
    }
}
