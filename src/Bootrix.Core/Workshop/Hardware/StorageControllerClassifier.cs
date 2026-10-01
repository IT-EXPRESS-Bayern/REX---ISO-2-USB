// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Workshop.Hardware;

public enum StorageControllerKind
{
    Other,
    Ide,
    Ahci,
    Nvme,

    /// <summary>Intel Volume Management Device: NVMe drives are hidden behind it.</summary>
    IntelVmd,

    /// <summary>Intel SATA/NVMe in RAID mode, or an Intel RAID-class controller whose ID is not in the table.</summary>
    IntelRstRaid,

    AmdRaid,

    /// <summary>RAID controller of another vendor (server cards, Marvell, ASMedia, ...).</summary>
    HardwareRaid,

    /// <summary>Virtio, PVSCSI: controllers of virtual machines that Windows has no driver for.</summary>
    VirtualStorage,
}

public static class StorageControllerClassifier
{
    private const byte MassStorage = 0x01;
    private const byte SubIde = 0x01;
    private const byte SubRaid = 0x04;
    private const byte SubSata = 0x06;
    private const byte SubNvm = 0x08;
    private const byte ProgIfAhci = 0x01;
    private const byte ProgIfNvme = 0x02;

    /// <summary>
    /// Order of evidence: exact ID tables, then the PCI class code, then the driver service. The class code outranks the
    /// service because Intel's RST driver also binds to controllers in plain AHCI mode, where Setup needs nothing extra.
    /// </summary>
    public static StorageControllerKind Classify(HardwareDevice device)
    {
        var service = device.Service?.ToLowerInvariant();

        if (device.Pci is { } id)
        {
            if (StorageControllerTable.IntelVmd.Contains(id))
            {
                return StorageControllerKind.IntelVmd;
            }

            if (StorageControllerTable.IntelRstRaid.Contains(id))
            {
                return StorageControllerKind.IntelRstRaid;
            }

            if (StorageControllerTable.VirtualStorage.Contains(id))
            {
                return StorageControllerKind.VirtualStorage;
            }

            if (id.Class is { Base: MassStorage } cc)
            {
                var fromClass = FromClass(cc, id.VendorId, service);
                if (fromClass != StorageControllerKind.Other)
                {
                    return fromClass;
                }
            }
        }

        return FromService(service);
    }

    private static StorageControllerKind FromClass(PciClassCode cc, ushort vendor, string? service)
    {
        switch (cc.SubClass)
        {
            case SubRaid when vendor == PciVendors.Intel:
                return service == "iastorvd" ? StorageControllerKind.IntelVmd : StorageControllerKind.IntelRstRaid;
            case SubRaid when vendor == PciVendors.Amd:
                return StorageControllerKind.AmdRaid;
            case SubRaid:
                return StorageControllerKind.HardwareRaid;
            case SubSata when cc.ProgrammingInterface == ProgIfAhci:
                return StorageControllerKind.Ahci;
            case SubNvm when cc.ProgrammingInterface is null or ProgIfNvme:
                return StorageControllerKind.Nvme;
            case SubIde:
                return StorageControllerKind.Ide;
            default:
                return StorageControllerKind.Other;
        }
    }

    private static StorageControllerKind FromService(string? service) => service switch
    {
        "iastorvd" => StorageControllerKind.IntelVmd,
        "iastorac" or "iastoravc" or "iastora" or "iastorv" => StorageControllerKind.IntelRstRaid,
        "rcraid" or "rcbottom" => StorageControllerKind.AmdRaid,
        "viostor" or "vioscsi" or "pvscsi" => StorageControllerKind.VirtualStorage,
        "storahci" or "msahci" => StorageControllerKind.Ahci,
        "stornvme" => StorageControllerKind.Nvme,
        "pciide" or "atapi" or "intelide" => StorageControllerKind.Ide,
        _ => StorageControllerKind.Other,
    };
}
