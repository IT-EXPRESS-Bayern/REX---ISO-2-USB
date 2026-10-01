// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.FileSystems.Fat;
using Bootrix.Core.Model;
using Bootrix.Core.Partitioning;

namespace Bootrix.Core.Planning;

/// <summary>
/// What will be written to the device, decided before anything is touched. The same plan drives
/// the dry-run preview, the layout shown to the user and the writers.
/// </summary>
public sealed record MediaPlan
{
    /// <summary>
    /// The partition table format Bootrix creates. <see cref="PartitionScheme.Auto"/> means none:
    /// a raw copy brings its own table, a superfloppy has no table at all.
    /// </summary>
    public PartitionScheme Scheme { get; init; } = PartitionScheme.Auto;

    public WriteMethod WriteMethod { get; init; }

    public BootMethod BootMethod { get; init; }

    /// <summary>The firmware the media is prepared for; <see cref="TargetFirmware.Auto"/> when the content is not bootable.</summary>
    public TargetFirmware Firmware { get; init; } = TargetFirmware.Auto;

    /// <summary>The file system starts at LBA 0 and there is no partition table.</summary>
    public bool Superfloppy { get; init; }

    /// <summary>Old-BIOS workarounds are in effect: classic offset, CHS-friendly types, capped size.</summary>
    public bool LegacyBios { get; init; }

    public IReadOnlyList<PlannedPartition> Partitions { get; init; } = [];

    /// <summary>A small FAT partition with the UEFI:NTFS driver sits behind the main partition.</summary>
    public bool UsesUefiNtfs { get; init; }

    /// <summary>The install.wim does not fit FAT32 and is split into .swm parts while copying.</summary>
    public bool SplitWim { get; init; }

    public bool NeedsPersistencePartition { get; init; }

    public bool WindowsToGo { get; init; }

    public int SectorSize { get; init; } = 512;

    public long DeviceBytes { get; init; }

    /// <summary>The CHS fiction used for MBR entries and for the BPB of every volume on the device.</summary>
    public ChsGeometry Geometry { get; init; } = ChsGeometry.Translated;

    public IReadOnlyList<PlanWarning> Warnings { get; init; } = [];

    public long TotalSectors => DeviceBytes / SectorSize;

    /// <summary>
    /// The formatter options for one FAT partition of this plan: its size, position, geometry and
    /// label. A superfloppy and a diskette get drive number 0, as their BIOS sees a floppy drive.
    /// </summary>
    public FatFormatOptions ToFatOptions(PlannedPartition partition)
    {
        ArgumentNullException.ThrowIfNull(partition);
        if (partition.FileSystem is not (FileSystemKind.Fat12 or FileSystemKind.Fat16 or FileSystemKind.Fat32))
        {
            throw new InvalidOperationException($"The {partition.Role} partition is not FAT.");
        }

        if (Superfloppy && FloppyPreset.FromSize(partition.LengthBytes) is { } floppy)
        {
            return floppy.ToOptions() with { Label = partition.Label };
        }

        return new FatFormatOptions
        {
            TotalBytes = partition.LengthBytes,
            BytesPerSector = SectorSize,
            Type = partition.FileSystem switch
            {
                FileSystemKind.Fat12 => FatType.Fat12,
                FileSystemKind.Fat16 => FatType.Fat16,
                _ => FatType.Fat32,
            },
            SectorsPerCluster = partition.ClusterSizeBytes / SectorSize,
            Label = partition.Label,
            HiddenSectors = (uint)Math.Min(partition.StartLba(SectorSize), uint.MaxValue),
            SectorsPerTrack = Geometry.SectorsPerTrack,
            Heads = Geometry.Heads,
            DriveNumber = Superfloppy ? (byte)0x00 : (byte)0x80,
        };
    }

    /// <summary>
    /// The partition table of this plan for writing into an image or handing to the platform layer.
    /// Not available for raw copies and superfloppies, which have no table of their own to describe.
    /// </summary>
    public DiskLayout ToDiskLayout(uint mbrSignature = 0, Guid? diskGuid = null, byte[]? bootstrap = null)
    {
        if (Scheme == PartitionScheme.Auto)
        {
            throw new InvalidOperationException("This plan does not create a partition table.");
        }

        return new DiskLayout
        {
            Scheme = Scheme,
            TotalSectors = TotalSectors,
            Partitions = [.. Partitions.Select(partition => partition.ToEntry(SectorSize))],
            MbrSignature = mbrSignature,
            DiskGuid = diskGuid ?? Guid.NewGuid(),
            Bootstrap = bootstrap,
            Geometry = Geometry,
        };
    }
}
