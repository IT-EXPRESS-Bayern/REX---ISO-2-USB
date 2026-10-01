// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Workshop.Hardware;

/// <summary>
/// Controllers that Windows Setup cannot use with its own drivers, or not reliably. The tables are
/// compiled from public references; each carries its source. They are a starting point, not a complete
/// list: <see cref="StorageControllerClassifier"/> falls back to the PCI class code for unknown IDs.
/// </summary>
public static class StorageControllerTable
{
    private const ushort Intel = 0x8086;
    private const ushort RedHat = 0x1AF4;
    private const ushort VMware = 0x15AD;

    /// <summary>
    /// Intel Volume Management Device. With VMD enabled in the firmware the NVMe drives sit behind this
    /// controller and are invisible to Setup until the Intel RST VMD driver (iaStorVD) is loaded.
    /// </summary>
    public static PciIdTable IntelVmd { get; } = new(
        "Intel Volume Management Device (VMD)",
        "Linux kernel drivers/pci/controller/vmd.c (vmd_ids); names from the PCI ID Database, pci-ids.ucw.cz",
        [
            new(Intel, 0x201D, "Volume Management Device NVMe RAID Controller"),
            new(Intel, 0x28C0, "Volume Management Device (VMD)"),
            new(Intel, 0x28C1, "Volume Management Device (VMD)"),
            new(Intel, 0x467F, "Volume Management Device NVMe RAID Controller"),
            new(Intel, 0x4C3D, "Volume Management Device NVMe RAID Controller"),
            new(Intel, 0x7D0B, "Core Ultra 200H/200V Series Processors VMD"),
            new(Intel, 0x9A0B, "Volume Management Device NVMe RAID Controller"),
            new(Intel, 0xA77F, "RST Volume Management Device Controller"),
            new(Intel, 0xAD0B, "Core Ultra 200 Series Processors VMD"),
            new(Intel, 0xB06F, "Volume Management Device (VMD)"),
            new(Intel, 0xB07F, "Volume Management Device (VMD)"),
            new(Intel, 0xB60B, "Volume Management Device (VMD)"),
            new(Intel, 0xD70B, "Core Ultra Processors (Series 4) VMD"),
            new(Intel, 0xD73B, "Volume Management Device (VMD)"),
        ]);

    /// <summary>
    /// Intel SATA controllers in RAID mode ("RAID On" / Intel RST Premium). The SATA ports and any NVMe drives
    /// remapped behind them may need the Intel RST driver (iaStorAC/iaStorAVC) in Setup; AHCI mode does not.
    /// In this mode the controller reports the RAID class code, which is why the Linux driver matches it by ID.
    /// </summary>
    public static PciIdTable IntelRstRaid { get; } = new(
        "Intel SATA controllers in RAID mode",
        "Linux kernel drivers/ata/ahci.c (ahci_pci_tbl, entries commented as RAID); names from the PCI ID Database, pci-ids.ucw.cz",
        [
            new(Intel, 0x02D7, "400 Series Chipset Family On-Package SATA Controller (RAID 0/1/5/10) premium"),
            new(Intel, 0x06D6, "Comet Lake PCH-H RAID"),
            new(Intel, 0x06D7, "400 Series Chipset Family SATA Controller (RAID 0/1/5/10) premium (Mobile)"),
            new(Intel, 0x1C04, "6 Series/C200 Series Desktop SATA RAID Controller"),
            new(Intel, 0x1C05, "6 Series/C200 Series Mobile SATA RAID Controller"),
            new(Intel, 0x1C06, "Z68 Express Chipset SATA RAID Controller"),
            new(Intel, 0x1C07, "CPT RAID (SATA, RAID mode)"),
            new(Intel, 0x1D04, "C600/X79 series chipset SATA RAID Controller"),
            new(Intel, 0x1D06, "C600/X79 series chipset SATA Premium RAID Controller"),
            new(Intel, 0x1E04, "7 Series/C210 Series Chipset Family SATA Controller [RAID mode]"),
            new(Intel, 0x1E05, "7 Series Chipset SATA Controller [RAID mode]"),
            new(Intel, 0x1E06, "7 Series/C210 Series Chipset Family SATA Controller [RAID mode]"),
            new(Intel, 0x1E07, "7 Series Chipset Family SATA Controller [RAID mode]"),
            new(Intel, 0x1E0E, "7 Series/C210 Series Chipset Family SATA Controller [RAID mode]"),
            new(Intel, 0x1F24, "Atom processor C2000 RAID SATA2 Controller"),
            new(Intel, 0x1F25, "Atom processor C2000 RAID SATA2 Controller"),
            new(Intel, 0x1F26, "Atom processor C2000 RAID SATA2 Controller"),
            new(Intel, 0x1F27, "Atom processor C2000 RAID SATA2 Controller"),
            new(Intel, 0x1F2E, "Atom processor C2000 RAID SATA2 Controller"),
            new(Intel, 0x1F2F, "Atom processor C2000 RAID SATA2 Controller"),
            new(Intel, 0x1F34, "Atom processor C2000 RAID SATA3 Controller"),
            new(Intel, 0x1F35, "Atom processor C2000 RAID SATA3 Controller"),
            new(Intel, 0x1F36, "Atom processor C2000 RAID SATA3 Controller"),
            new(Intel, 0x1F37, "Atom processor C2000 RAID SATA3 Controller"),
            new(Intel, 0x1F3E, "Atom processor C2000 RAID SATA3 Controller"),
            new(Intel, 0x1F3F, "Atom processor C2000 RAID SATA3 Controller"),
            new(Intel, 0x2822, "SATA Controller (RAID 0/1/5/10) In-box Compatible ID (Desktop RST)"),
            new(Intel, 0x2826, "SATA Controller (RAID 0/1/5/10) In-box Compatible ID (Server/Desktop RST)"),
            new(Intel, 0x2827, "SSATA Controller (RAID 0/1/5/10)"),
            new(Intel, 0x282F, "tSATA Controller [RAID Mode]"),
            new(Intel, 0x3B24, "PCH RAID (SATA, RAID mode)"),
            new(Intel, 0x3B25, "5 Series/3400 Series Chipset SATA RAID Controller"),
            new(Intel, 0x3B2B, "PCH RAID (SATA, RAID mode)"),
            new(Intel, 0x3B2C, "5 Series/3400 Series Chipset SATA RAID Controller"),
            new(Intel, 0x43D4, "500 Series Chipset Family SATA Controller (RAID 0/1/5/10) no premium (Desktop)"),
            new(Intel, 0x43D5, "500 Series Chipset Family SATA Controller (RAID 0/1/5/10) no premium (Mobile)"),
            new(Intel, 0x43D6, "500 Series Chipset Family SATA Controller (RAID 0/1/5/10) premium (Server/Desktop)"),
            new(Intel, 0x43D7, "500 Series Chipset Family SATA Controller (RAID 0/1/5/10) premium (Mobile)"),
            new(Intel, 0x8C04, "8 Series/C220 Series Chipset Family SATA Controller 1 [RAID mode]"),
            new(Intel, 0x8C05, "8 Series/C220 Series Chipset Family SATA Controller 1 [RAID mode]"),
            new(Intel, 0x8C06, "8 Series/C220 Series Chipset Family SATA Controller 1 [RAID mode]"),
            new(Intel, 0x8C07, "8 Series/C220 Series Chipset Family SATA Controller 1 [RAID mode]"),
            new(Intel, 0x8C0E, "8 Series/C220 Series Chipset Family SATA Controller 1 [RAID mode]"),
            new(Intel, 0x8C0F, "8 Series/C220 Series Chipset Family SATA Controller 1 [RAID mode]"),
            new(Intel, 0x8C84, "9 Series Chipset Family SATA Controller [RAID Mode]"),
            new(Intel, 0x8C85, "9 Series Chipset Family SATA Controller [RAID Mode]"),
            new(Intel, 0x8C86, "9 Series Chipset Family SATA Controller [RAID Mode]"),
            new(Intel, 0x8C87, "9 Series Chipset Family SATA Controller [RAID Mode]"),
            new(Intel, 0x8C8E, "9 Series Chipset Family SATA Controller [RAID Mode]"),
            new(Intel, 0x8C8F, "9 Series Chipset Family SATA Controller [RAID Mode]"),
            new(Intel, 0x8D04, "C610/X99 series chipset SATA Controller [RAID mode]"),
            new(Intel, 0x8D06, "C610/X99 series chipset SATA Controller [RAID mode]"),
            new(Intel, 0x8D0E, "C610/X99 series chipset SATA Controller [RAID mode]"),
            new(Intel, 0x8D64, "C610/X99 series chipset sSATA Controller [RAID mode]"),
            new(Intel, 0x8D66, "C610/X99 series chipset sSATA Controller [RAID mode]"),
            new(Intel, 0x8D6E, "C610/X99 series chipset sSATA Controller [RAID mode]"),
            new(Intel, 0x9C04, "8 Series SATA Controller 1 [RAID mode]"),
            new(Intel, 0x9C05, "8 Series SATA Controller 1 [RAID mode]"),
            new(Intel, 0x9C06, "8 Series SATA Controller 1 [RAID mode]"),
            new(Intel, 0x9C07, "8 Series SATA Controller 1 [RAID mode]"),
            new(Intel, 0x9C0E, "8 Series SATA Controller 1 [RAID mode]"),
            new(Intel, 0x9C0F, "8 Series SATA Controller 1 [RAID mode]"),
            new(Intel, 0x9C85, "Wildcat Point-LP SATA Controller [RAID Mode]"),
            new(Intel, 0x9C87, "Wildcat Point-LP SATA Controller [RAID Mode]"),
            new(Intel, 0x9C8F, "Wildcat Point-LP SATA Controller [RAID Mode]"),
            new(Intel, 0x9D05, "Sunrise LP RAID (SATA, RAID mode)"),
            new(Intel, 0x9D07, "Sunrise LP RAID (SATA, RAID mode)"),
            new(Intel, 0xA105, "Sunrise Point-H SATA Controller [RAID mode]"),
            new(Intel, 0xA106, "Q170/H170/Z170/CM236 Chipset SATA Controller [RAID Mode]"),
            new(Intel, 0xA107, "HM170/QM170 Chipset SATA Controller [RAID Mode]"),
            new(Intel, 0xA10F, "Sunrise Point-H SATA Controller [RAID mode]"),
            new(Intel, 0xA186, "C620 Series Chipset Family SATA Controller (RAID 0/1/5/10)"),
            new(Intel, 0xA1D6, "C620 Series Chipset Family SSATA Controller (RAID 0/1/5/10)"),
            new(Intel, 0xA206, "C620 Series Chipset Family (Super) SATA Controller (RAID 0/1/5/10)"),
            new(Intel, 0xA256, "C620 Series Chipset Family (Super) SSATA Controller (RAID 0/1/5/10)"),
            new(Intel, 0xA356, "300/C240 Series Chipset Family SATA Controller (RAID 0/1/5/10)"),
            new(Intel, 0xA386, "B460/H410 Chipset SATA Controller (RAID 0/1/5/10) Premium"),
        ]);

    /// <summary>Storage controllers of virtual machines that Windows has no driver for.</summary>
    public static PciIdTable VirtualStorage { get; } = new(
        "Virtual storage controllers without a Windows inbox driver",
        "OASIS Virtio 1.x specification, section 4.1.2 (PCI device IDs: 0x1000-0x103F transitional, 0x1040 + device type modern); VMware PVSCSI from pci-ids.ucw.cz",
        [
            new(RedHat, 0x1001, "Virtio block device (transitional)"),
            new(RedHat, 0x1042, "Virtio block device"),
            new(RedHat, 0x1004, "Virtio SCSI (transitional)"),
            new(RedHat, 0x1048, "Virtio SCSI"),
            new(VMware, 0x07C0, "PVSCSI SCSI Controller"),
        ]);

    /// <summary>Network adapters of virtual machines that Windows has no driver for.</summary>
    public static PciIdTable VirtualNetwork { get; } = new(
        "Virtual network adapters without a Windows inbox driver",
        "OASIS Virtio 1.x specification, section 4.1.2; VMware VMXNET3 from pci-ids.ucw.cz",
        [
            new(RedHat, 0x1000, "Virtio network device (transitional)"),
            new(RedHat, 0x1041, "Virtio network device"),
            new(VMware, 0x07B0, "VMXNET3 Ethernet Controller"),
        ]);
}
