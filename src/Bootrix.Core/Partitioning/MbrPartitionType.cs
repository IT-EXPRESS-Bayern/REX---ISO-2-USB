// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Partitioning;

/// <summary>Partition type bytes of the MBR scheme. The CHS and LBA variants differ only in how old boot code reads the volume.</summary>
public static class MbrPartitionType
{
    public const byte Empty = 0x00;

    /// <summary>FAT12, within the first 32 MB.</summary>
    public const byte Fat12 = 0x01;

    /// <summary>FAT16 below 65,536 sectors.</summary>
    public const byte Fat16Small = 0x04;

    public const byte Extended = 0x05;

    /// <summary>FAT16B read through CHS; MS-DOS 6.22 and earlier know nothing else, and it must lie within the first 8 GB.</summary>
    public const byte Fat16 = 0x06;

    /// <summary>NTFS, exFAT, HPFS, UDF and ReFS all share this byte.</summary>
    public const byte Ntfs = 0x07;

    public const byte Fat32Chs = 0x0B;

    public const byte Fat32Lba = 0x0C;

    public const byte Fat16Lba = 0x0E;

    public const byte ExtendedLba = 0x0F;

    public const byte LinuxSwap = 0x82;

    public const byte Linux = 0x83;

    public const byte LinuxLvm = 0x8E;

    public const byte AppleBoot = 0xAB;

    /// <summary>HFS and HFS+.</summary>
    public const byte AppleHfs = 0xAF;

    /// <summary>Marks a GPT disk to MBR-only tools; the entry spans the whole disk.</summary>
    public const byte GptProtective = 0xEE;

    /// <summary>UEFI firmware treats such a partition as an EFI System Partition.</summary>
    public const byte EfiSystem = 0xEF;

    /// <summary>The one-track "BIOS compatibility" partition some old BIOSes want to see after the data.</summary>
    public const byte BiosCompatibility = 0xEA;

    public static bool IsExtended(byte type) => type is Extended or ExtendedLba or 0x85;
}
