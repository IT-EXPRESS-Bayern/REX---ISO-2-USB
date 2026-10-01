// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Workshop.Hardware;

public enum DeviceCategory
{
    Other,
    Storage,
    Network,
    Display,
}

/// <summary>The driver package bound to a device, as far as Windows reports it.</summary>
public sealed record DriverPackageInfo
{
    public string? InfName { get; init; }

    public string? Provider { get; init; }

    public string? Version { get; init; }

    public DateOnly? Date { get; init; }

    /// <summary>
    /// True for oemNN.inf: the package was added to the driver store by someone other than Windows, so the
    /// same driver is not necessarily part of the next Windows installation media.
    /// </summary>
    public bool? IsVendorPackage => InfName is null ? null : IsOemInf(InfName);

    public static bool IsOemInf(string infName)
    {
        // The Core runs on Linux too, where Path.GetFileName does not split at a backslash.
        var name = infName[(infName.LastIndexOfAny(['\\', '/']) + 1)..];
        return name.Length > 7
            && name.StartsWith("oem", StringComparison.OrdinalIgnoreCase)
            && name.EndsWith(".inf", StringComparison.OrdinalIgnoreCase)
            && name.AsSpan(3, name.Length - 7).IndexOfAnyExceptInRange('0', '9') < 0;
    }
}

public sealed record HardwareDevice
{
    private static readonly string[] WirelessMarkers = ["Wi-Fi", "WiFi", "Wireless", "WLAN", "802.11"];

    public required string InstanceId { get; init; }

    public string? Name { get; init; }

    public string? Manufacturer { get; init; }

    /// <summary>Windows setup class such as Net, SCSIAdapter, HDC or Display; empty for devices without a driver.</summary>
    public string? ClassName { get; init; }

    public IReadOnlyList<string> HardwareIds { get; init; } = [];

    public IReadOnlyList<string> CompatibleIds { get; init; } = [];

    /// <summary>Name of the kernel service (driver) bound to the device, e.g. iaStorVD.</summary>
    public string? Service { get; init; }

    /// <summary>Configuration Manager problem code; 0 means the device works, 28 means no driver is installed. Null when unknown.</summary>
    public int? ProblemCode { get; init; }

    public DriverPackageInfo? Driver { get; init; }

    public PciId? Pci => PciId.From(HardwareIds, CompatibleIds);

    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? InstanceId : Name;

    public DeviceCategory Category
    {
        get
        {
            // The PCI class code is more reliable than the setup class, which is empty while no driver is installed.
            var pciBase = Pci?.Class?.Base;
            return pciBase switch
            {
                0x01 => DeviceCategory.Storage,
                0x02 => DeviceCategory.Network,
                0x03 => DeviceCategory.Display,
                _ => ClassName?.ToUpperInvariant() switch
                {
                    "SCSIADAPTER" or "HDC" => DeviceCategory.Storage,
                    "NET" => DeviceCategory.Network,
                    "DISPLAY" => DeviceCategory.Display,
                    _ => DeviceCategory.Other,
                },
            };
        }
    }

    /// <summary>PCI network controllers of subclass 0x80 are what Wi-Fi chips report; the name decides for USB devices.</summary>
    public bool IsWireless => Category == DeviceCategory.Network
        && (Pci?.Class is { Base: 0x02, SubClass: 0x80 } || LooksWireless(Name));

    private static bool LooksWireless(string? name) =>
        name is not null && WirelessMarkers.Any(m => name.Contains(m, StringComparison.OrdinalIgnoreCase));
}
