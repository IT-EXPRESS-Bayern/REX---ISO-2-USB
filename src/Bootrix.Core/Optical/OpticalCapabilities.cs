// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Optical;

[Flags]
public enum OpticalCapabilities
{
    None = 0,
    CdR = 1 << 0,
    CdRw = 1 << 1,
    DvdPlusR = 1 << 2,
    DvdPlusRw = 1 << 3,
    DvdPlusRDualLayer = 1 << 4,
    DvdPlusRwDualLayer = 1 << 5,
    DvdMinusR = 1 << 6,
    DvdMinusRw = 1 << 7,
    DvdMinusRDualLayer = 1 << 8,
    DvdRam = 1 << 9,
    BdR = 1 << 10,
    BdRe = 1 << 11,
}

public static class OpticalCapabilitiesExtensions
{
    /// <summary>
    /// Maps an MMC feature profile number (as listed in IDiscRecorder2.SupportedProfiles) to the
    /// write capability it stands for. Read-only profiles, HD DVD and unknown numbers add nothing.
    /// </summary>
    public static OpticalCapabilities FromMmcProfile(int profile) => profile switch
    {
        0x09 => OpticalCapabilities.CdR,
        0x0A => OpticalCapabilities.CdRw,
        0x11 => OpticalCapabilities.DvdMinusR,
        0x12 => OpticalCapabilities.DvdRam,
        0x13 or 0x14 => OpticalCapabilities.DvdMinusRw,
        0x15 or 0x16 => OpticalCapabilities.DvdMinusRDualLayer,
        0x1A => OpticalCapabilities.DvdPlusRw,
        0x1B => OpticalCapabilities.DvdPlusR,
        0x2A => OpticalCapabilities.DvdPlusRwDualLayer,
        0x2B => OpticalCapabilities.DvdPlusRDualLayer,
        0x41 or 0x42 => OpticalCapabilities.BdR,
        0x43 => OpticalCapabilities.BdRe,
        _ => OpticalCapabilities.None,
    };

    public static OpticalCapabilities FromMmcProfiles(IEnumerable<int> profiles) =>
        profiles.Aggregate(OpticalCapabilities.None, (all, profile) => all | FromMmcProfile(profile));

    public static bool CanWrite(this OpticalCapabilities capabilities, OpticalMediaType type)
    {
        var needed = type switch
        {
            OpticalMediaType.CdR => OpticalCapabilities.CdR,
            OpticalMediaType.CdRw => OpticalCapabilities.CdRw,
            OpticalMediaType.DvdRam => OpticalCapabilities.DvdRam,
            OpticalMediaType.DvdPlusR => OpticalCapabilities.DvdPlusR,
            OpticalMediaType.DvdPlusRw => OpticalCapabilities.DvdPlusRw,
            OpticalMediaType.DvdPlusRDualLayer => OpticalCapabilities.DvdPlusRDualLayer,
            OpticalMediaType.DvdPlusRwDualLayer => OpticalCapabilities.DvdPlusRwDualLayer,
            OpticalMediaType.DvdMinusR => OpticalCapabilities.DvdMinusR,
            OpticalMediaType.DvdMinusRw => OpticalCapabilities.DvdMinusRw,
            OpticalMediaType.DvdMinusRDualLayer => OpticalCapabilities.DvdMinusRDualLayer,
            OpticalMediaType.BdR => OpticalCapabilities.BdR,
            OpticalMediaType.BdRe => OpticalCapabilities.BdRe,
            _ => OpticalCapabilities.None,
        };

        return needed != OpticalCapabilities.None && capabilities.HasFlag(needed);
    }
}
