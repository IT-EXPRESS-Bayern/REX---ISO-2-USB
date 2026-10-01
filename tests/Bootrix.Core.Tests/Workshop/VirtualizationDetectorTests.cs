// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Workshop.Firmware;
using Bootrix.Core.Workshop.Hardware;

namespace Bootrix.Core.Tests.Workshop;

public class VirtualizationDetectorTests
{
    [Theory]
    [InlineData("Microsoft Corporation", "Virtual Machine", VirtualMachineKind.HyperV)]
    [InlineData("VMware, Inc.", "VMware7,1", VirtualMachineKind.VMware)]
    [InlineData("VMware, Inc.", "VMware Virtual Platform", VirtualMachineKind.VMware)]
    [InlineData("innotek GmbH", "VirtualBox", VirtualMachineKind.VirtualBox)]
    [InlineData("Oracle Corporation", "VirtualBox", VirtualMachineKind.VirtualBox)]
    [InlineData("QEMU", "Standard PC (Q35 + ICH9, 2009)", VirtualMachineKind.QemuKvm)]
    [InlineData("Red Hat", "KVM", VirtualMachineKind.QemuKvm)]
    [InlineData("Xen", "HVM domU", VirtualMachineKind.Xen)]
    [InlineData("Parallels Software International Inc.", "Parallels Virtual Platform", VirtualMachineKind.Parallels)]
    [InlineData("Amazon EC2", "m5.large", VirtualMachineKind.Other)]
    [InlineData("Dell Inc.", "Latitude 5430", VirtualMachineKind.None)]
    [InlineData("LENOVO", "20XW0055GE", VirtualMachineKind.None)]
    [InlineData("Microsoft Corporation", "Surface Laptop 5", VirtualMachineKind.None)]
    public void Detect_UsesTheSmbiosStrings(string maker, string model, VirtualMachineKind expected)
    {
        Assert.Equal(expected, VirtualizationDetector.Detect(maker, model, null, null));
    }

    [Fact]
    public void Detect_HyperVHostOnPhysicalHardware_IsNotAVirtualMachine()
    {
        // Windows with Hyper-V or core isolation runs on the hypervisor too and shows "Microsoft Hv" without being a guest.
        Assert.Equal(VirtualMachineKind.None, VirtualizationDetector.Detect("Dell Inc.", "XPS 15 9520", "Microsoft Hv", true));
    }

    [Theory]
    [InlineData("VMwareVMware", VirtualMachineKind.VMware)]
    [InlineData("VBoxVBoxVBox", VirtualMachineKind.VirtualBox)]
    [InlineData("KVMKVMKVM", VirtualMachineKind.QemuKvm)]
    [InlineData("TCGTCGTCGTCG", VirtualMachineKind.QemuKvm)]
    [InlineData("XenVMMXenVMM", VirtualMachineKind.Xen)]
    [InlineData("prl hyperv", VirtualMachineKind.Parallels)]
    public void Detect_FallsBackToTheHypervisorVendor(string vendor, VirtualMachineKind expected)
    {
        Assert.Equal(expected, VirtualizationDetector.Detect(null, null, vendor, true));
    }

    [Fact]
    public void Detect_CustomisedSmbiosOnAKnownHypervisor_StillFindsTheVm()
    {
        Assert.Equal(VirtualMachineKind.QemuKvm, VirtualizationDetector.Detect("ACME Cloud", "vm-standard-2", "KVMKVMKVM", true));
    }

    [Fact]
    public void Detect_NothingKnown_IsUnknown()
    {
        Assert.Equal(VirtualMachineKind.Unknown, VirtualizationDetector.Detect(null, null, null, null));
    }

    [Fact]
    public void Detect_NoHypervisorAndNoSmbios_IsNone()
    {
        Assert.Equal(VirtualMachineKind.None, VirtualizationDetector.Detect(null, null, null, false));
    }

    [Fact]
    public void Detect_HyperVVendorWithoutSmbios_StaysUnknown()
    {
        Assert.Equal(VirtualMachineKind.Unknown, VirtualizationDetector.Detect(null, null, "Microsoft Hv", true));
    }

    [Theory]
    [InlineData(null, ChassisKind.Unknown)]
    [InlineData(1, ChassisKind.Unknown)]
    [InlineData(2, ChassisKind.Unknown)]
    [InlineData(3, ChassisKind.Desktop)]
    [InlineData(7, ChassisKind.Desktop)]
    [InlineData(9, ChassisKind.Laptop)]
    [InlineData(10, ChassisKind.Laptop)]
    [InlineData(13, ChassisKind.AllInOne)]
    [InlineData(17, ChassisKind.Server)]
    [InlineData(23, ChassisKind.Server)]
    [InlineData(28, ChassisKind.Server)]
    [InlineData(30, ChassisKind.Tablet)]
    [InlineData(31, ChassisKind.Laptop)]
    [InlineData(35, ChassisKind.MiniPc)]
    [InlineData(36, ChassisKind.MiniPc)]
    [InlineData(34, ChassisKind.Embedded)]
    [InlineData(12, ChassisKind.Other)]
    public void ChassisKinds_MapTheSmbiosCodes(int? code, ChassisKind expected)
    {
        Assert.Equal(expected, ChassisKinds.FromSmbios((byte?)code));
    }
}
