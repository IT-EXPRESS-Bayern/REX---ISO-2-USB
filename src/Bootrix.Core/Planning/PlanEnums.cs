// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Planning;

public enum DeviceBus
{
    Unknown,
    Usb,
    Sata,
    Ata,
    Nvme,
    Sd,
    Mmc,
    Scsi,
    Ieee1394,
    Virtual,
}

public enum DeviceMedium
{
    Stick,
    Card,
    Hdd,
    Floppy,
}

public enum WriteMethod
{
    /// <summary>Copy the image sector by sector; its own partition table and boot code are used as they are.</summary>
    RawCopy,

    /// <summary>Create the partitions, format them and copy the files of the image into the main partition.</summary>
    ExtractFiles,

    /// <summary>Create the partitions and format them; boot files come from Bootrix itself (FreeDOS) or there are none.</summary>
    FormatOnly,

    /// <summary>Apply the Windows image onto the main partition and make it bootable, i.e. Windows To Go.</summary>
    ApplyImage,
}

[Flags]
public enum BootMethod
{
    None = 0,

    /// <summary>Boot sector of the Windows boot manager on an active MBR partition.</summary>
    WindowsBootmgrBios = 1,

    /// <summary>The firmware loads \EFI\BOOT\BOOT*.EFI from a FAT partition.</summary>
    UefiNative = 2,

    /// <summary>A small FAT partition with the UEFI:NTFS driver chain-loads the real loader from NTFS or exFAT.</summary>
    UefiNtfs = 4,

    /// <summary>Syslinux in the file system plus the Syslinux MBR (or gptmbr on GPT).</summary>
    SyslinuxMbr = 8,

    /// <summary>GRUB 2 with boot.img in the MBR and core.img in the gap before the first partition or in a BIOS boot partition.</summary>
    Grub = 16,

    FreeDos = 32,

    /// <summary>Boot code and partition table come from the image that is copied raw.</summary>
    ImageNative = 64,
}

public enum PartitionRole
{
    /// <summary>The partition that holds the image's files, or the whole volume of a data stick.</summary>
    Main,

    /// <summary>EFI System Partition of a Windows To Go drive.</summary>
    Esp,

    /// <summary>Microsoft Reserved Partition, only on GPT drives that boot Windows from the device itself.</summary>
    Msr,

    /// <summary>The 1 MiB FAT partition with the UEFI:NTFS driver.</summary>
    UefiNtfs,

    /// <summary>GRUB's embedding area on a GPT disk.</summary>
    BiosBoot,

    Persistence,
}
