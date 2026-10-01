// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images.Udif;

namespace Bootrix.Core.Images.Apple;

/// <summary>What the Apple-specific inspection of an image found.</summary>
public sealed record AppleImageInfo
{
    public required AppleImageContainer Container { get; init; }

    public required AppleImageKind Kind { get; init; }

    public required AppleImageScheme Scheme { get; init; }

    /// <summary>The file system of the volume (bare volumes) or of the first Mac volume found.</summary>
    public required AppleFileSystem FileSystem { get; init; }

    /// <summary>Size of the decoded volume in bytes.</summary>
    public required long VolumeSize { get; init; }

    /// <summary>The image carries something a Mac or an El Torito BIOS starts from: a blessed folder, Mac boot support partitions or a boot catalog.</summary>
    public required bool IsBootable { get; init; }

    /// <summary>Only Mac partitions and file systems and no PC boot code: of no use on anything but a Mac.</summary>
    public required bool IsMacOnly { get; init; }

    public required bool HasIso9660 { get; init; }

    public required IReadOnlyList<AppleImagePartition> Partitions { get; init; }

    public required IReadOnlyList<AppleImageHint> Hints { get; init; }

    public DmgInfo? Dmg { get; init; }
}
