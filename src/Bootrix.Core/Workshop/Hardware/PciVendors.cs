// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Workshop.Hardware;

/// <summary>Vendor names for the IDs that show up in storage, network and virtual devices; the full list is the PCI ID Database (pci-ids.ucw.cz).</summary>
public static class PciVendors
{
    public const ushort Intel = 0x8086;
    public const ushort Amd = 0x1022;

    private static readonly Dictionary<ushort, string> Names = new()
    {
        [0x8086] = "Intel",
        [0x1022] = "AMD",
        [0x1002] = "AMD (ATI)",
        [0x10DE] = "NVIDIA",
        [0x10EC] = "Realtek",
        [0x14C3] = "MediaTek",
        [0x168C] = "Qualcomm Atheros",
        [0x17CB] = "Qualcomm",
        [0x1969] = "Qualcomm Atheros (Killer)",
        [0x14E4] = "Broadcom",
        [0x1B4B] = "Marvell",
        [0x1000] = "Broadcom (LSI)",
        [0x9005] = "Adaptec (Microchip)",
        [0x103C] = "Hewlett-Packard",
        [0x1028] = "Dell",
        [0x144D] = "Samsung",
        [0x15B7] = "SanDisk",
        [0x126F] = "Silicon Motion",
        [0x1B21] = "ASMedia",
        [0x1414] = "Microsoft",
        [0x15AD] = "VMware",
        [0x1AF4] = "Red Hat (virtio)",
        [0x1B36] = "Red Hat (QEMU)",
        [0x1234] = "QEMU (Bochs)",
        [0x80EE] = "Oracle VirtualBox",
        [0x5853] = "XenSource",
        [0x1AB8] = "Parallels",
    };

    public static string? NameOf(ushort vendorId) => Names.GetValueOrDefault(vendorId);
}
