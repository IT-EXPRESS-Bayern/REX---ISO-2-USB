// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using Bootrix.Core.Workshop.Hardware;

namespace Bootrix.Windows.Workshop;

/// <summary>
/// Lists the PCI devices and USB network adapters with their hardware IDs, the problem code and the driver package bound to
/// them. Only storage, network and display devices are kept; the hundred other bridges and controllers say nothing about Setup.
/// </summary>
internal static class PnpDeviceReader
{
    public static IReadOnlyList<HardwareDevice> Read(IssueLog issues)
    {
        var entities = Wmi.Select(
            "SELECT DeviceID, Name, Manufacturer, PNPClass, Service, HardwareID, CompatibleID, ConfigManagerErrorCode FROM Win32_PnPEntity",
            e => new
            {
                Id = Wmi.GetString(e, "DeviceID"),
                Name = Wmi.GetString(e, "Name"),
                Manufacturer = Wmi.GetString(e, "Manufacturer"),
                Class = Wmi.GetString(e, "PNPClass"),
                Service = Wmi.GetString(e, "Service"),
                HardwareIds = Wmi.GetStrings(e, "HardwareID"),
                CompatibleIds = Wmi.GetStrings(e, "CompatibleID"),
                Problem = Wmi.GetUInt(e, "ConfigManagerErrorCode"),
            });

        var drivers = ReadDrivers(issues);
        var devices = new List<HardwareDevice>();
        foreach (var entity in entities)
        {
            if (entity.Id is null || !IsInteresting(entity.Id, entity.Class))
            {
                continue;
            }

            var device = new HardwareDevice
            {
                InstanceId = entity.Id,
                Name = entity.Name,
                Manufacturer = entity.Manufacturer,
                ClassName = entity.Class,
                HardwareIds = entity.HardwareIds,
                CompatibleIds = entity.CompatibleIds,
                Service = entity.Service,
                ProblemCode = entity.Problem is { } problem ? (int)problem : null,
                Driver = drivers.GetValueOrDefault(entity.Id),
            };

            if (device.Category != DeviceCategory.Other)
            {
                devices.Add(device);
            }
        }

        return devices;
    }

    private static bool IsInteresting(string instanceId, string? className) =>
        instanceId.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)
        || (instanceId.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) && string.Equals(className, "Net", StringComparison.OrdinalIgnoreCase));

    /// <summary>Win32_PnPSignedDriver is slow, so a failure here must not cost the device list.</summary>
    private static Dictionary<string, DriverPackageInfo> ReadDrivers(IssueLog issues)
    {
        var map = new Dictionary<string, DriverPackageInfo>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var rows = Wmi.Select(
                "SELECT DeviceID, InfName, DriverProviderName, DriverVersion, DriverDate FROM Win32_PnPSignedDriver",
                d => (Id: Wmi.GetString(d, "DeviceID"), Package: new DriverPackageInfo
                {
                    InfName = Wmi.GetString(d, "InfName"),
                    Provider = Wmi.GetString(d, "DriverProviderName"),
                    Version = Wmi.GetString(d, "DriverVersion"),
                    Date = ParseCimDate(Wmi.GetString(d, "DriverDate")),
                }));

            foreach (var (id, package) in rows)
            {
                if (id is not null)
                {
                    map[id] = package;
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            issues.Add("drivers", ex);
        }

        return map;
    }

    /// <summary>CIM date-times look like 20230315000000.000000+000; the day is all that matters here.</summary>
    internal static DateOnly? ParseCimDate(string? text) =>
        text is { Length: >= 8 } && DateOnly.TryParseExact(text[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
}
