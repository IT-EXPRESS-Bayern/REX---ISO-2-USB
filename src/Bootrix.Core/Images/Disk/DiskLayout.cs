// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Images.Disk;

public sealed record MbrPartition(int Slot, byte Type, bool Active, long StartSector, long SectorCount)
{
    public long EndSector => StartSector + SectorCount;

    public bool IsEfiSystem => Type == 0xEF;

    public bool IsProtective => Type == 0xEE;
}

public sealed record GptPartition(Guid TypeId, Guid UniqueId, long FirstSector, long LastSector, string Name)
{
    public bool IsEfiSystem => TypeId == GptTypes.EfiSystem;
}

/// <summary>
/// Partition tables found at the start of an image. For a hybrid ISO this describes what a firmware sees
/// once the image has been written to a disk; for a raw disk image it is the image's own layout.
/// </summary>
public sealed record DiskLayout
{
    /// <summary>The 0x55AA signature is present in the first sector.</summary>
    public bool HasMbrSignature { get; init; }

    /// <summary>The first 440 bytes are not all zero.</summary>
    public bool HasBootCode { get; init; }

    public IReadOnlyList<MbrPartition> MbrPartitions { get; init; } = [];

    public bool HasGpt { get; init; }

    /// <summary>True when the GPT header's CRC-32 matches.</summary>
    public bool GptHeaderValid { get; init; }

    /// <summary>Logical sector size the GPT was written for (512 or 4096); 0 without a GPT.</summary>
    public int GptSectorSize { get; init; }

    /// <summary>LBA of the backup GPT header as recorded in the primary header.</summary>
    public long GptBackupSector { get; init; }

    public long GptLastUsableSector { get; init; }

    public Guid GptDiskId { get; init; }

    public IReadOnlyList<GptPartition> GptPartitions { get; init; } = [];

    /// <summary>Apple Partition Map signature ("ER") in the first block; the entries themselves are not parsed.</summary>
    public bool HasApm { get; init; }

    /// <summary>
    /// The first sector is a FAT boot sector (a floppy or superfloppy image) rather than a partition table.
    /// </summary>
    public bool IsVolumeImage { get; init; }

    public bool HasProtectiveMbr => MbrPartitions.Any(partition => partition.IsProtective);

    /// <summary>True when the MBR or the GPT lists an EFI system partition, so UEFI can boot a raw copy of the image.</summary>
    public bool HasEfiSystemPartition => MbrPartitions.Any(partition => partition.IsEfiSystem) || GptPartitions.Any(partition => partition.IsEfiSystem);

    public bool HasPartitionTable => MbrPartitions.Any(partition => !partition.IsProtective) || HasGpt;

    /// <summary>The image can be written to a disk as is and the firmware will find a partition table.</summary>
    public bool IsBootableDisk => HasMbrSignature && !IsVolumeImage && (HasBootCode || HasEfiSystemPartition) && HasPartitionTable;
}
