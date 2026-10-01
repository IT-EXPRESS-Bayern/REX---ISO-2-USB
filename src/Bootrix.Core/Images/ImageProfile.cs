// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Images;

public enum ImageKind
{
    Unknown,
    WindowsSetup,
    WindowsPe,
    LinuxHybrid,
    LinuxIsoOnly,
    Bsd,
    Dos,
    Apple,
    RawDisk,
    Data,

    /// <summary>Other operating systems that are installed from extracted files: ESXi, ReactOS, KolibriOS.</summary>
    OtherOs,
}

public enum WindowsArch
{
    Unknown,
    X86,
    X64,
    Arm64,

    /// <summary>32-bit ARM (Windows RT, early Windows 10 on ARM).</summary>
    Arm,
}

/// <summary>
/// What the planner needs to know about an image. Produced by the image inspector, consumed by
/// the layout planner; neither knows about the other's internals.
/// </summary>
public sealed record ImageProfile
{
    public ImageKind Kind { get; init; } = ImageKind.Unknown;

    /// <summary>Distribution family such as "ubuntu", "debian", "fedora", "arch"; null when not recognised.</summary>
    public string? Family { get; init; }

    public string? VolumeLabel { get; init; }

    /// <summary>Size of the data a file copy has to place on the target (all files of the image); for raw images the image length.</summary>
    public long TotalBytes { get; init; }

    public long LargestFileBytes { get; init; }

    /// <summary>True when sources\install.wim (or a comparable file) does not fit on FAT32.</summary>
    public bool HasFileOver4GiB => LargestFileBytes >= 4L * 1024 * 1024 * 1024 - 1;

    /// <summary>The image starts with a valid MBR or GPT and can be written to a disk as is.</summary>
    public bool IsHybrid { get; init; }

    public bool HasBiosBootFiles { get; init; }

    public bool HasEfiBootFiles { get; init; }

    public bool HasElToritoBios { get; init; }

    public bool HasElToritoEfi { get; init; }

    /// <summary>An El Torito entry boots a floppy or hard disk image (DOS tool discs, firmware updaters); such discs need MEMDISK or a raw copy of that image.</summary>
    public bool HasEmulatedBootImage { get; init; }

    public WindowsArch Arch { get; init; } = WindowsArch.Unknown;

    /// <summary>Windows build number from the WIM metadata, e.g. 26200; 0 when unknown.</summary>
    public int WindowsBuild { get; init; }

    public ImageContainer Container { get; init; }

    /// <summary>The partition table lists an EFI system partition, so UEFI can boot a raw copy of the image.</summary>
    public bool HasEspPartition { get; init; }

    /// <summary>Length of the image as it is written in raw (DD) mode; 0 when it is not known, as with most compressed images.</summary>
    public long ImageBytes { get; init; }

    public bool IsCompressed { get; init; }
}
