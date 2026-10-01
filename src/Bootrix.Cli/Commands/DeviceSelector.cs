// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Storage;

namespace Bootrix.Cli.Commands;

internal static class DeviceSelector
{
    public static readonly DiskFilter ListAll = new()
    {
        IncludeUsbHardDisks = true,
        IncludeInternalDisks = true,
        IncludeVirtualDisks = true,
        IncludeBlocked = true,
        IncludeEmptyReaders = true,
    };

    /// <summary>Finds a disk by number ("3"), by name ("disk3") or by serial number; the match must be unique.</summary>
    public static StorageDevice Resolve(IDiskService disks, string spec, DiskFilter? filter = null)
    {
        var all = disks.Enumerate(filter ?? ListAll);
        var wanted = spec.Trim();
        if (wanted.StartsWith("disk", StringComparison.OrdinalIgnoreCase))
        {
            wanted = wanted[4..];
        }

        var matches = int.TryParse(wanted, out var number)
            ? all.Where(d => d.DiskNumber == number).ToList()
            : all.Where(d => string.Equals(d.Serial, wanted, StringComparison.OrdinalIgnoreCase)).ToList();

        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new BootrixException(ErrorCode.DeviceNotFound, spec),
            _ => throw new BootrixException(ErrorCode.DeviceChanged, $"'{spec}' matches {matches.Count} disks"),
        };
    }
}
