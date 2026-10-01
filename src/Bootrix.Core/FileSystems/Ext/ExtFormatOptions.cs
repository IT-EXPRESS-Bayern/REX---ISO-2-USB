// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.FileSystems.Ext;

public sealed record ExtFormatOptions
{
    public ExtFileSystemType Type { get; init; } = ExtFileSystemType.Ext3;

    /// <summary>Volume label, at most 16 bytes in UTF-8. Live systems look for "casper-rw", "writable" or "persistence".</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Random when not set. Also seeds the directory hash so equal options give identical images.</summary>
    public Guid? Uuid { get; init; }

    /// <summary>File system size; the stream length when not set. A longer stream is not touched beyond this size.</summary>
    public long? SizeBytes { get; init; }

    /// <summary>1024, 2048 or 4096. Automatic: 1024 below 128 MiB, 4096 above.</summary>
    public int? BlockSize { get; init; }

    /// <summary>Inode density. Automatic: 4096 below 512 MiB, 16384 above.</summary>
    public int? BytesPerInode { get; init; }

    /// <summary>128 or 256. 128-byte inodes cannot store dates after 2038 and no creation time.</summary>
    public int InodeSize { get; init; } = 256;

    /// <summary>Share of the blocks only root may allocate. Persistence stores are not system volumes, so none by default.</summary>
    public double ReservedBlocksPercent { get; init; }

    /// <summary>Journal size for ext3 and ext4. Automatic: 1/64 of the volume between 1024 and 32768 blocks.</summary>
    public long? JournalSizeBytes { get; init; }

    /// <summary>
    /// Flags unused groups as uninitialized (uninit_bg) and leaves their inode tables unwritten, so formatting
    /// stays fast on large media; the kernel zeroes the tables in the background after the first mount.
    /// When off, every inode table is written out and the journal is cleared.
    /// </summary>
    public bool LazyInitialization { get; init; } = true;

    /// <summary>The target is known to read as zeros (a new sparse file); nothing has to be cleared.</summary>
    public bool AssumeZeroedTarget { get; init; }

    public TimeProvider? TimeProvider { get; init; }

    /// <summary>Files created in the root directory right after formatting.</summary>
    public IReadOnlyList<ExtRootFile> RootFiles { get; init; } = [];
}
