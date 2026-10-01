// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Workshop.Firmware;
using Bootrix.Core.Workshop.Hardware;

namespace Bootrix.Core.Workshop;

/// <summary>Who built the machine, from SMBIOS. Placeholders such as "To Be Filled By O.E.M." are left out.</summary>
public sealed record MachineIdentity
{
    public string? Manufacturer { get; init; }

    public string? Model { get; init; }

    public string? Version { get; init; }

    public string? SerialNumber { get; init; }

    public string? Sku { get; init; }

    public string? Family { get; init; }

    public Guid? Uuid { get; init; }

    public string? BoardManufacturer { get; init; }

    public string? BoardModel { get; init; }

    public string? BoardVersion { get; init; }

    public string? BoardSerialNumber { get; init; }

    public byte? ChassisType { get; init; }

    public ChassisKind Chassis => ChassisKinds.FromSmbios(ChassisType);

    public VirtualMachineKind VirtualMachine { get; init; }

    public string DisplayName
    {
        get
        {
            var name = $"{Manufacturer} {Model}".Trim();
            return name.Length > 0 ? name : $"{BoardManufacturer} {BoardModel}".Trim();
        }
    }

    public static MachineIdentity From(SmbiosData smbios, VirtualMachineKind virtualMachine) => new()
    {
        Manufacturer = smbios.SystemManufacturer,
        Model = smbios.SystemProductName,
        Version = smbios.SystemVersion,
        SerialNumber = smbios.SystemSerialNumber,
        Sku = smbios.SystemSku,
        Family = smbios.SystemFamily,
        Uuid = smbios.SystemUuid,
        BoardManufacturer = smbios.BoardManufacturer,
        BoardModel = smbios.BoardProduct,
        BoardVersion = smbios.BoardVersion,
        BoardSerialNumber = smbios.BoardSerialNumber,
        ChassisType = smbios.ChassisType,
        VirtualMachine = virtualMachine,
    };
}
