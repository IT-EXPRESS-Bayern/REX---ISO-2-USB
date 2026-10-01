// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Optical;

/// <summary>Physical disc types with the numbering of IMAPI_MEDIA_PHYSICAL_TYPE, so the Windows layer can cast.</summary>
public enum OpticalMediaType
{
    Unknown = 0,
    CdRom = 1,
    CdR = 2,
    CdRw = 3,
    DvdRom = 4,
    DvdRam = 5,
    DvdPlusR = 6,
    DvdPlusRw = 7,
    DvdPlusRDualLayer = 8,
    DvdMinusR = 9,
    DvdMinusRw = 10,
    DvdMinusRDualLayer = 11,
    Disk = 12,
    DvdPlusRwDualLayer = 13,
    HdDvdRom = 14,
    HdDvdR = 15,
    HdDvdRam = 16,
    BdRom = 17,
    BdR = 18,
    BdRe = 19,
}

public enum OpticalMediaFamily
{
    Unknown,
    Cd,
    Dvd,
    BluRay,
}

public static class OpticalMediaTypeExtensions
{
    public static OpticalMediaFamily Family(this OpticalMediaType type) => type switch
    {
        OpticalMediaType.CdRom or OpticalMediaType.CdR or OpticalMediaType.CdRw => OpticalMediaFamily.Cd,
        OpticalMediaType.DvdRom or OpticalMediaType.DvdRam or OpticalMediaType.DvdPlusR or OpticalMediaType.DvdPlusRw
            or OpticalMediaType.DvdPlusRDualLayer or OpticalMediaType.DvdMinusR or OpticalMediaType.DvdMinusRw
            or OpticalMediaType.DvdMinusRDualLayer or OpticalMediaType.DvdPlusRwDualLayer => OpticalMediaFamily.Dvd,
        OpticalMediaType.BdRom or OpticalMediaType.BdR or OpticalMediaType.BdRe => OpticalMediaFamily.BluRay,
        _ => OpticalMediaFamily.Unknown,
    };

    /// <summary>Media that can be erased and written again.</summary>
    public static bool IsRewritable(this OpticalMediaType type) => type is
        OpticalMediaType.CdRw or OpticalMediaType.DvdRam or OpticalMediaType.DvdPlusRw
        or OpticalMediaType.DvdMinusRw or OpticalMediaType.DvdPlusRwDualLayer or OpticalMediaType.BdRe;

    /// <summary>
    /// Media a recorder can write at all. IMAPI cannot burn HD DVD, and a ROM disc never takes data.
    /// </summary>
    public static bool IsWritable(this OpticalMediaType type) => type is
        OpticalMediaType.CdR or OpticalMediaType.CdRw or OpticalMediaType.DvdRam
        or OpticalMediaType.DvdPlusR or OpticalMediaType.DvdPlusRw or OpticalMediaType.DvdPlusRDualLayer
        or OpticalMediaType.DvdMinusR or OpticalMediaType.DvdMinusRw or OpticalMediaType.DvdMinusRDualLayer
        or OpticalMediaType.DvdPlusRwDualLayer or OpticalMediaType.BdR or OpticalMediaType.BdRe;

    /// <summary>Discs where the capacity reported by the drive is the formatted size and says little about the file system on it.</summary>
    public static bool HasFormattedCapacity(this OpticalMediaType type) => type.IsRewritable() && type != OpticalMediaType.CdRw;
}
