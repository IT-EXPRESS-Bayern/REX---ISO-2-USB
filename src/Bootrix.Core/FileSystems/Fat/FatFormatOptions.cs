// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.FileSystems.Fat;

public sealed record FatFormatOptions
{
    /// <summary>Size of the volume; a partial trailing sector is ignored.</summary>
    public required long TotalBytes { get; init; }

    /// <summary>Logical sector size of the device: 512, or 4096 for 4Kn media.</summary>
    public int BytesPerSector { get; init; } = 512;

    /// <summary>Null picks the Microsoft default for the volume size, adjusted until the cluster count fits the FAT type.</summary>
    public int? SectorsPerCluster { get; init; }

    /// <summary>Null means 1 for FAT12/16 and 32 for FAT32 (raised so the data area starts on a 1 MiB boundary).</summary>
    public int? ReservedSectors { get; init; }

    public int FatCount { get; init; } = 2;

    /// <summary>
    /// Null chooses by volume size. A given type is checked against the cluster count it would
    /// produce, because that count alone defines the type to every FAT driver.
    /// </summary>
    public FatType? Type { get; init; }

    /// <summary>Null means 512 on FAT12/16; ignored (always 0) on FAT32.</summary>
    public int? RootEntries { get; init; }

    /// <summary>Up to 11 characters; folded to upper case and to the OEM code page. Null or empty leaves the volume unlabeled.</summary>
    public string? Label { get; init; }

    /// <summary>Null derives the serial from the current date and time the way Windows format does.</summary>
    public uint? VolumeId { get; init; }

    /// <summary>Start of the partition on the disk; boot code uses it to find the volume.</summary>
    public uint HiddenSectors { get; init; }

    public int SectorsPerTrack { get; init; } = 63;

    public int Heads { get; init; } = 255;

    /// <summary>0xF8 for fixed media, 0xF0 for 3.5-inch floppies.</summary>
    public byte MediaDescriptor { get; init; } = 0xF8;

    /// <summary>0x80 for disks, 0x00 for floppies and superfloppy sticks.</summary>
    public byte DriveNumber { get; init; } = 0x80;

    public string OemName { get; init; } = "MSWIN4.1";

    /// <summary>
    /// A boot sector image from another source (FreeDOS, Syslinux, a Windows-formatted volume).
    /// Its code replaces ours; the BPB fields stay those computed here. Either one 512-byte sector,
    /// or on FAT32 a whole number of sectors that fill reserved sectors 0, 1, 2, ... in order.
    /// </summary>
    public byte[]? BootCode { get; init; }

    public TimeProvider? TimeProvider { get; init; }

    /// <summary>
    /// The target is known to read back zeros (a fresh sparse file, a freshly cleared disk), so the
    /// FAT and root directory are not overwritten with zeros. Saves gigabytes of writes on huge FAT32 volumes.
    /// </summary>
    public bool AssumeZeroed { get; init; }
}
