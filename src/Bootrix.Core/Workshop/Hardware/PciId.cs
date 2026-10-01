// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Bootrix.Core.Workshop.Hardware;

/// <summary>PCI class code as Windows reports it in CC_ccssPP; the programming interface is absent in the short form CC_ccss.</summary>
public readonly record struct PciClassCode(byte Base, byte SubClass, byte? ProgrammingInterface = null)
{
    public override string ToString() => ProgrammingInterface is { } pi
        ? $"{Base:X2}{SubClass:X2}{pi:X2}"
        : $"{Base:X2}{SubClass:X2}";
}

/// <summary>The identifiers Windows derives from PCI configuration space and exposes as hardware and compatible IDs.</summary>
public readonly record struct PciId(ushort VendorId, ushort DeviceId, uint? SubsystemId = null, byte? Revision = null, PciClassCode? Class = null)
{
    /// <summary>Short form used to match driver packages, e.g. "PCI\VEN_8086&amp;DEV_9A0B".</summary>
    public string HardwareId => $"PCI\\VEN_{VendorId:X4}&DEV_{DeviceId:X4}";

    public bool Matches(ushort vendor, ushort device) => VendorId == vendor && DeviceId == device;

    public override string ToString() => $"{VendorId:X4}:{DeviceId:X4}";

    /// <summary>
    /// Combines the hardware IDs (vendor, device, subsystem, revision) and the compatible IDs (class code) of one device.
    /// Returns null when none of the IDs is a PCI ID with vendor and device.
    /// </summary>
    public static PciId? From(IEnumerable<string> hardwareIds, IEnumerable<string>? compatibleIds = null)
    {
        ushort? vendor = null;
        ushort? device = null;
        uint? subsystem = null;
        byte? revision = null;
        PciClassCode? classCode = null;

        foreach (var id in hardwareIds.Concat(compatibleIds ?? []))
        {
            if (!id.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var token in id[4..].Split('&'))
            {
                if (TryValue(token, "VEN_", 4, out var v))
                {
                    vendor ??= (ushort)v;
                }
                else if (TryValue(token, "DEV_", 4, out v))
                {
                    device ??= (ushort)v;
                }
                else if (TryValue(token, "SUBSYS_", 8, out v))
                {
                    subsystem ??= v;
                }
                else if (TryValue(token, "REV_", 2, out v))
                {
                    revision ??= (byte)v;
                }
                else if (token.StartsWith("CC_", StringComparison.OrdinalIgnoreCase))
                {
                    classCode = Longer(classCode, ParseClass(token.AsSpan(3)));
                }
            }
        }

        // A class-only ID such as PCI\CC_0104 has no vendor; the device is then not identifiable.
        return vendor is null || device is null ? null : new PciId(vendor.Value, device.Value, subsystem, revision, classCode);
    }

    private static PciClassCode? ParseClass(ReadOnlySpan<char> digits)
    {
        if (digits.Length is not (4 or 6) || !uint.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
        {
            return null;
        }

        return digits.Length == 6
            ? new PciClassCode((byte)(value >> 16), (byte)(value >> 8), (byte)value)
            : new PciClassCode((byte)(value >> 8), (byte)value);
    }

    /// <summary>The six-digit form carries the programming interface, so it wins over the four-digit form.</summary>
    private static PciClassCode? Longer(PciClassCode? current, PciClassCode? candidate)
    {
        if (candidate is null)
        {
            return current;
        }

        return current is { ProgrammingInterface: not null } ? current : candidate;
    }

    private static bool TryValue(string token, string prefix, int digits, out uint value)
    {
        value = 0;
        return token.Length == prefix.Length + digits
            && token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && uint.TryParse(token.AsSpan(prefix.Length), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }
}
