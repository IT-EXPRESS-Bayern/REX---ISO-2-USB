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
}

public enum WindowsArch
{
    Unknown,
    X86,
    X64,
    Arm64,
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

    public WindowsArch Arch { get; init; } = WindowsArch.Unknown;

    /// <summary>Windows build number from the WIM metadata, e.g. 26200; 0 when unknown.</summary>
    public int WindowsBuild { get; init; }
}
