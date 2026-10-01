// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Catalog.Rescue;

/// <summary>How the downloaded image should be put on a stick. A recommendation; the image inspector has the last word.</summary>
public enum RescueWriteMode
{
    /// <summary>There is no image: the entry points to a vendor tool or page.</summary>
    None,

    /// <summary>The image is not known well enough to recommend a mode; decide from the file once it is on disk.</summary>
    Auto,

    /// <summary>The file is a complete disk image (after unpacking) and is copied byte for byte.</summary>
    RawDd,

    /// <summary>An ISO with its own partition table, so it can be copied byte for byte like a disk image.</summary>
    IsoHybrid,

    /// <summary>An ISO without a partition table: the files are extracted onto a partition and a boot loader is installed.</summary>
    IsoExtract,

    /// <summary>A Windows PE image (boot.wim): extracted onto FAT32, or NTFS with UEFI:NTFS when files exceed 4 GiB.</summary>
    WindowsPe,

    /// <summary>The ISO is loop-booted by GRUB on multiboot media instead of being written on its own.</summary>
    GrubChain,
}
