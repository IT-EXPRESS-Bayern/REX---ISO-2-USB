// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Workshop.Firmware;

namespace Bootrix.Windows.Workshop;

internal static class SmbiosReader
{
    /// <summary>Returns null when the firmware offers no SMBIOS table, as in some virtual machines and Windows PE images.</summary>
    public static SmbiosData? Read() =>
        FirmwareTables.Read(FirmwareTables.SmbiosProvider, 0) is { } raw ? SmbiosParser.ParseRawFirmwareTable(raw) : null;
}
