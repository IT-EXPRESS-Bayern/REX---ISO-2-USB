// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Workshop.Hardware;

public enum VirtualMachineKind
{
    Unknown,
    None,
    HyperV,
    VMware,
    VirtualBox,
    QemuKvm,
    Xen,
    Parallels,
    Other,
}

public static class VirtualizationDetector
{
    /// <summary>
    /// The SMBIOS strings decide first. The CPUID hypervisor bit alone is not enough: a Windows host with Hyper-V or
    /// virtualization-based security also runs on top of the hypervisor and reports "Microsoft Hv".
    /// </summary>
    public static VirtualMachineKind Detect(string? manufacturer, string? model, string? hypervisorVendor, bool? hypervisorPresent)
    {
        var fromSmbios = FromSmbios(manufacturer, model);
        if (fromSmbios != VirtualMachineKind.None)
        {
            return fromSmbios;
        }

        if (hypervisorPresent == true)
        {
            var fromCpu = FromHypervisorVendor(hypervisorVendor);
            if (fromCpu != VirtualMachineKind.Unknown)
            {
                return fromCpu;
            }
        }

        var smbiosKnown = !string.IsNullOrWhiteSpace(manufacturer) || !string.IsNullOrWhiteSpace(model);
        return smbiosKnown || hypervisorPresent == false ? VirtualMachineKind.None : VirtualMachineKind.Unknown;
    }

    private static VirtualMachineKind FromSmbios(string? manufacturer, string? model)
    {
        var maker = manufacturer ?? "";
        var product = model ?? "";

        if (Has(maker, "Microsoft") && Has(product, "Virtual Machine"))
        {
            return VirtualMachineKind.HyperV;
        }

        if (Has(maker, "VMware") || Has(product, "VMware"))
        {
            return VirtualMachineKind.VMware;
        }

        if (Has(product, "VirtualBox") || Has(maker, "innotek"))
        {
            return VirtualMachineKind.VirtualBox;
        }

        if (Has(maker, "QEMU") || Has(product, "KVM") || Has(product, "Standard PC (Q35") || Has(product, "Standard PC (i440FX"))
        {
            return VirtualMachineKind.QemuKvm;
        }

        if (maker.Equals("Xen", StringComparison.OrdinalIgnoreCase) || Has(product, "HVM domU"))
        {
            return VirtualMachineKind.Xen;
        }

        if (Has(maker, "Parallels"))
        {
            return VirtualMachineKind.Parallels;
        }

        if (Has(maker, "Amazon EC2") || Has(maker, "OpenStack") || Has(product, "Google Compute Engine"))
        {
            return VirtualMachineKind.Other;
        }

        return VirtualMachineKind.None;
    }

    private static VirtualMachineKind FromHypervisorVendor(string? vendor) => vendor switch
    {
        "VMwareVMware" => VirtualMachineKind.VMware,
        "VBoxVBoxVBox" => VirtualMachineKind.VirtualBox,
        "KVMKVMKVM" or "TCGTCGTCGTCG" => VirtualMachineKind.QemuKvm,
        "XenVMMXenVMM" => VirtualMachineKind.Xen,
        "prl hyperv" => VirtualMachineKind.Parallels,
        _ => VirtualMachineKind.Unknown,
    };

    private static bool Has(string text, string fragment) => text.Contains(fragment, StringComparison.OrdinalIgnoreCase);
}
