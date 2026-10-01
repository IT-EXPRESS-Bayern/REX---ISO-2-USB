// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Images;

/// <summary>The outermost structure of an image once any compression has been removed.</summary>
public enum ImageContainer
{
    Unknown,

    /// <summary>ISO 9660, with Joliet or Rock Ridge extensions where present.</summary>
    Iso9660,

    /// <summary>ISO 9660 and UDF side by side (the layout of all Windows ISOs).</summary>
    IsoUdfBridge,

    /// <summary>UDF without an ISO 9660 side.</summary>
    Udf,

    /// <summary>A disk image that starts with an MBR or GPT.</summary>
    RawDisk,

    /// <summary>A bare FAT volume: floppy and superfloppy images.</summary>
    FatVolume,

    Wim,
    Vhd,
    Vhdx,

    /// <summary>Windows full-flash update image.</summary>
    Ffu,

    /// <summary>Apple disk image (UDIF .dmg or .sparseimage); the contents are analysed elsewhere.</summary>
    AppleImage,
}
