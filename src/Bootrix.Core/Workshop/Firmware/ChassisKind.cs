// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Workshop.Firmware;

public enum ChassisKind
{
    Unknown,
    Desktop,
    Laptop,
    Tablet,
    AllInOne,
    MiniPc,
    Server,
    Embedded,
    Other,
}

public static class ChassisKinds
{
    /// <summary>Maps the SMBIOS chassis type (DSP0134 table 17). "Other" and "Unknown" codes stay unknown: many desktop boards report them.</summary>
    public static ChassisKind FromSmbios(byte? type) => type switch
    {
        null or 0x01 or 0x02 => ChassisKind.Unknown,
        0x03 or 0x04 or 0x05 or 0x06 or 0x07 or 0x0F or 0x10 or 0x18 => ChassisKind.Desktop,
        0x08 or 0x09 or 0x0A or 0x0E or 0x1F => ChassisKind.Laptop,
        0x0B or 0x1E or 0x20 => ChassisKind.Tablet,
        0x0D => ChassisKind.AllInOne,
        0x23 or 0x24 => ChassisKind.MiniPc,
        0x11 or 0x16 or 0x17 or 0x19 or 0x1A or 0x1B or 0x1C or 0x1D => ChassisKind.Server,
        0x21 or 0x22 => ChassisKind.Embedded,
        _ => ChassisKind.Other,
    };
}
