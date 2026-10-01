// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Workshop.Firmware;

/// <summary>The fields of SMBIOS structure types 0 (BIOS), 1 (system), 2 (base board) and 3 (chassis) that identify a machine.</summary>
public sealed record SmbiosData
{
    public int MajorVersion { get; init; }

    public int MinorVersion { get; init; }

    public string Version => $"{MajorVersion}.{MinorVersion}";

    public string? BiosVendor { get; init; }

    public string? BiosVersion { get; init; }

    public DateOnly? BiosReleaseDate { get; init; }

    public string? SystemManufacturer { get; init; }

    public string? SystemProductName { get; init; }

    public string? SystemVersion { get; init; }

    public string? SystemSerialNumber { get; init; }

    public string? SystemSku { get; init; }

    public string? SystemFamily { get; init; }

    public Guid? SystemUuid { get; init; }

    public string? BoardManufacturer { get; init; }

    public string? BoardProduct { get; init; }

    public string? BoardVersion { get; init; }

    public string? BoardSerialNumber { get; init; }

    /// <summary>Chassis type code without the lock bit (3 = desktop, 9 = laptop, 23 = rack mount, ...).</summary>
    public byte? ChassisType { get; init; }
}
