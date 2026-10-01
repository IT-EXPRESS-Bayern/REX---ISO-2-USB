// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using Bootrix.Core.Errors;

namespace Bootrix.Core.FileSystems.Ext;

/// <summary>Options resolved against the volume size: geometry, feature flags, identifiers and journal size.</summary>
internal sealed class ExtFormatPlan
{
    private const long SmallVolumeBytes = 128L << 20;
    private const long SmallInodeRatioBytes = 512L << 20;
    private const long MaxJournalBlocks = 1 << 22;
    private const int MinBlocks = 64;
    private const int LabelBytes = 16;

    // Space beyond the fixed overhead that the root, lost+found and the block maps need.
    private const int SlackBlocks = 24;

    private ExtFormatPlan(ExtFormatOptions options, ExtLayout layout, long journalBlocks, byte[] uuid, byte[] label)
    {
        Options = options;
        Layout = layout;
        JournalBlocks = journalBlocks;
        Uuid = uuid;
        Label = label;
        HashSeed = SHA256.HashData(uuid)[..16];
    }

    public ExtFormatOptions Options { get; }

    public ExtLayout Layout { get; }

    public long JournalBlocks { get; }

    public byte[] Uuid { get; }

    public byte[] HashSeed { get; }

    public byte[] Label { get; }

    public ExtFileSystemType Type => Options.Type;

    public bool Lazy => Options.LazyInitialization;

    public uint Compat
    {
        get
        {
            var flags = ExtFeatures.CompatExtAttr | ExtFeatures.CompatDirIndex;
            return JournalBlocks > 0 ? flags | ExtFeatures.CompatHasJournal : flags;
        }
    }

    public uint Incompat => Type == ExtFileSystemType.Ext4
        ? ExtFeatures.IncompatFileType | ExtFeatures.IncompatExtents
        : ExtFeatures.IncompatFileType;

    public uint RoCompat
    {
        get
        {
            var flags = ExtFeatures.RoCompatSparseSuper | ExtFeatures.RoCompatLargeFile;
            if (Lazy)
            {
                flags |= ExtFeatures.RoCompatGroupChecksums;
            }

            if (Type == ExtFileSystemType.Ext4)
            {
                flags |= ExtFeatures.RoCompatHugeFile | ExtFeatures.RoCompatDirNlink;
                if (Layout.InodeSize > ExtInode.BaseSize)
                {
                    flags |= ExtFeatures.RoCompatExtraIsize;
                }
            }

            return flags;
        }
    }

    public static ExtFormatPlan Create(ExtFormatOptions options, long sizeBytes)
    {
        if (!Enum.IsDefined(options.Type))
        {
            throw Invalid($"Unknown file system type {options.Type}.");
        }

        var label = Encoding.UTF8.GetBytes(options.Label);
        if (label.Length > LabelBytes || label.Contains((byte)0))
        {
            throw Invalid($"The label must not be longer than {LabelBytes} bytes in UTF-8 and must not contain NUL.");
        }

        var blockSize = options.BlockSize ?? (sizeBytes < SmallVolumeBytes ? 1024 : 4096);
        if (blockSize is not (1024 or 2048 or 4096))
        {
            throw Invalid($"Block size {blockSize} is not supported; use 1024, 2048 or 4096.");
        }

        if (options.InodeSize is not (128 or 256))
        {
            throw Invalid($"Inode size {options.InodeSize} is not supported; use 128 or 256.");
        }

        var bytesPerInode = options.BytesPerInode ?? (sizeBytes < SmallInodeRatioBytes ? 4096 : 16384);
        if (bytesPerInode < blockSize)
        {
            throw Invalid("The bytes-per-inode ratio must not be smaller than the block size.");
        }

        if (options.ReservedBlocksPercent is < 0 or > 50 || double.IsNaN(options.ReservedBlocksPercent))
        {
            throw Invalid("The reserved block percentage must be between 0 and 50.");
        }

        if (options.Type == ExtFileSystemType.Ext2 && options.JournalSizeBytes is not null)
        {
            throw Invalid("A journal size was given for an ext2 file system, which has no journal.");
        }

        if (sizeBytes / blockSize < MinBlocks)
        {
            throw TooSmall((long)MinBlocks * blockSize, sizeBytes);
        }

        var layout = ExtLayout.Plan(sizeBytes, blockSize, bytesPerInode, options.InodeSize);
        var journalBlocks = options.Type == ExtFileSystemType.Ext2 ? 0 : ChooseJournalBlocks(options, layout);
        var uuid = (options.Uuid ?? Guid.NewGuid()).ToByteArray(bigEndian: true);

        var plan = new ExtFormatPlan(options, layout, journalBlocks, uuid, label);
        plan.EnsureFits(sizeBytes);
        return plan;
    }

    private static long ChooseJournalBlocks(ExtFormatOptions options, ExtLayout layout)
    {
        if (options.JournalSizeBytes is not { } requested)
        {
            return Math.Clamp(layout.BlockCount / 64, ExtJournal.MinBlocks, 32768);
        }

        var blocks = (requested + layout.BlockSize - 1) / layout.BlockSize;
        if (blocks < ExtJournal.MinBlocks || blocks > MaxJournalBlocks || blocks > layout.BlockCount / 2)
        {
            throw Invalid($"The journal must have between {ExtJournal.MinBlocks} blocks and half of the volume, and at most {MaxJournalBlocks} blocks.");
        }

        return blocks;
    }

    private void EnsureFits(long sizeBytes)
    {
        long overhead = 0;
        for (var group = 0; group < Layout.GroupCount; group++)
        {
            overhead += Layout.GroupOverhead(group);
        }

        var lostFound = Math.Max(1, 16 * 1024 / Layout.BlockSize);
        var journal = JournalBlocks > 0 ? JournalBlocks + ExtBlockMap.MappingBlockCount(JournalBlocks, Layout.BlockSize) : 0;
        var required = (overhead + 1 + lostFound + journal + SlackBlocks) * Layout.BlockSize;
        if (Layout.BlockCount * Layout.BlockSize < required)
        {
            throw TooSmall(required, sizeBytes);
        }
    }

    private static BootrixException Invalid(string detail) =>
        new(ErrorCode.InvalidSpec, detail) { Arguments = [detail] };

    private static BootrixException TooSmall(long required, long available) =>
        new(ErrorCode.DeviceTooSmall, $"The volume of {available} bytes is smaller than the {required} bytes the file system needs.")
        {
            Arguments = [ExtVolume.FormatBytes(required), ExtVolume.FormatBytes(available)],
        };
}
