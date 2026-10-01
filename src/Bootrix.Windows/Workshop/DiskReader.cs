// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using Bootrix.Core.Storage;
using Bootrix.Core.Workshop;

namespace Bootrix.Windows.Workshop;

/// <summary>Disks from the shared disk service, with the media type (SSD or HDD) the storage subsystem knows.</summary>
internal static class DiskReader
{
    private const string StorageScope = @"root\Microsoft\Windows\Storage";

    public static IReadOnlyList<DiskInfo> Read(IDiskService diskService, IssueLog issues)
    {
        var media = ReadMediaTypes(issues);
        var devices = diskService.Enumerate(new DiskFilter
        {
            IncludeInternalDisks = true,
            IncludeUsbHardDisks = true,
            IncludeVirtualDisks = true,
            IncludeBlocked = true,
            IncludeEmptyReaders = false,
        });

        return [.. devices.Select(d => DiskInfo.From(d, media.GetValueOrDefault(d.DiskNumber)))];
    }

    /// <summary>MSFT_PhysicalDisk numbers its disks like the disk service does: DeviceId is the disk number.</summary>
    private static Dictionary<int, DiskMediaType> ReadMediaTypes(IssueLog issues)
    {
        var map = new Dictionary<int, DiskMediaType>();
        try
        {
            var rows = Wmi.Select(
                "SELECT DeviceId, MediaType FROM MSFT_PhysicalDisk",
                d => (Id: Wmi.GetString(d, "DeviceId"), Type: Wmi.GetUInt(d, "MediaType")),
                StorageScope);

            foreach (var (id, type) in rows)
            {
                if (int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                {
                    map[number] = type switch
                    {
                        3 => DiskMediaType.Hdd,
                        4 => DiskMediaType.Ssd,
                        5 => DiskMediaType.StorageClassMemory,
                        _ => DiskMediaType.Unknown,
                    };
                }
            }
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            issues.Add("disk-media", ex);
        }

        return map;
    }
}
