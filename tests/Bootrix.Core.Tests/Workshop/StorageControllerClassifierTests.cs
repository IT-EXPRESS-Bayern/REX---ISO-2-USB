// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Workshop.Hardware;

namespace Bootrix.Core.Tests.Workshop;

public class StorageControllerClassifierTests
{
    private static HardwareDevice Device(string[] hardwareIds, string[]? compatibleIds = null, string? service = null, string? className = null) => new()
    {
        InstanceId = hardwareIds[0] + @"\3&11583659&0&E0",
        Name = "Controller",
        HardwareIds = hardwareIds,
        CompatibleIds = compatibleIds ?? [],
        Service = service,
        ClassName = className,
    };

    [Theory]
    [InlineData("9A0B", "010400")]
    [InlineData("467F", "010400")]
    [InlineData("A77F", "010800")]
    public void KnownVmdControllers_AreRecognisedByIdWhateverTheirClass(string device, string classCode)
    {
        var controller = Device([$@"PCI\VEN_8086&DEV_{device}"], [$@"PCI\VEN_8086&CC_{classCode}", $@"PCI\CC_{classCode}"]);

        Assert.Equal(StorageControllerKind.IntelVmd, StorageControllerClassifier.Classify(controller));
    }

    [Fact]
    public void UnknownIntelRaidClassController_IsTreatedAsRstRaid()
    {
        var controller = Device([@"PCI\VEN_8086&DEV_FFFF"], [@"PCI\CC_010400"]);

        Assert.Equal(StorageControllerKind.IntelRstRaid, StorageControllerClassifier.Classify(controller));
    }

    [Fact]
    public void UnknownIntelRaidClassController_WithVmdDriver_IsVmd()
    {
        var controller = Device([@"PCI\VEN_8086&DEV_FFFF"], [@"PCI\CC_010400"], service: "iaStorVD");

        Assert.Equal(StorageControllerKind.IntelVmd, StorageControllerClassifier.Classify(controller));
    }

    [Theory]
    [InlineData("A106")]
    [InlineData("A105")]
    [InlineData("2822")]
    public void IntelSataInRaidMode_IsRstRaid(string device)
    {
        var controller = Device([$@"PCI\VEN_8086&DEV_{device}"], [@"PCI\CC_010400"], service: "iaStorAC");

        Assert.Equal(StorageControllerKind.IntelRstRaid, StorageControllerClassifier.Classify(controller));
    }

    [Fact]
    public void IntelSataInAhciMode_NeedsNothingEvenWithTheRstDriverInstalled()
    {
        // iaStorAC also binds to AHCI controllers once Intel RST is installed; Setup can use such a controller without it.
        var controller = Device([@"PCI\VEN_8086&DEV_A102"], [@"PCI\CC_010601", @"PCI\CC_0106"], service: "iaStorAC");

        Assert.Equal(StorageControllerKind.Ahci, StorageControllerClassifier.Classify(controller));
    }

    [Fact]
    public void StandardNvme_IsNvme()
    {
        var controller = Device([@"PCI\VEN_144D&DEV_A808"], [@"PCI\CC_010802", @"PCI\CC_0108"], service: "stornvme");

        Assert.Equal(StorageControllerKind.Nvme, StorageControllerClassifier.Classify(controller));
    }

    [Fact]
    public void AmdRaidClass_IsAmdRaid()
    {
        var controller = Device([@"PCI\VEN_1022&DEV_7916"], [@"PCI\CC_010400"]);

        Assert.Equal(StorageControllerKind.AmdRaid, StorageControllerClassifier.Classify(controller));
    }

    [Fact]
    public void ServerRaidCard_IsHardwareRaid()
    {
        var controller = Device([@"PCI\VEN_1000&DEV_005D&SUBSYS_1F471028"], [@"PCI\CC_010400"]);

        Assert.Equal(StorageControllerKind.HardwareRaid, StorageControllerClassifier.Classify(controller));
    }

    [Theory]
    [InlineData("1AF4", "1001")]
    [InlineData("1AF4", "1042")]
    [InlineData("1AF4", "1004")]
    [InlineData("1AF4", "1048")]
    [InlineData("15AD", "07C0")]
    public void VirtualControllers_AreVirtualStorage(string vendor, string device)
    {
        var controller = Device([$@"PCI\VEN_{vendor}&DEV_{device}"], [@"PCI\CC_0100"]);

        Assert.Equal(StorageControllerKind.VirtualStorage, StorageControllerClassifier.Classify(controller));
    }

    [Fact]
    public void Ide_IsIde()
    {
        var controller = Device([@"PCI\VEN_8086&DEV_2921"], [@"PCI\CC_010180", @"PCI\CC_0101"]);

        Assert.Equal(StorageControllerKind.Ide, StorageControllerClassifier.Classify(controller));
    }

    [Theory]
    [InlineData("iaStorVD", StorageControllerKind.IntelVmd)]
    [InlineData("iaStorAVC", StorageControllerKind.IntelRstRaid)]
    [InlineData("rcraid", StorageControllerKind.AmdRaid)]
    [InlineData("viostor", StorageControllerKind.VirtualStorage)]
    [InlineData("storahci", StorageControllerKind.Ahci)]
    [InlineData("stornvme", StorageControllerKind.Nvme)]
    [InlineData("somethingelse", StorageControllerKind.Other)]
    public void WithoutPciClass_TheDriverServiceDecides(string service, StorageControllerKind expected)
    {
        var controller = Device(["ACPI\\VEN_INT&DEV_33A1"], service: service, className: "SCSIAdapter");

        Assert.Equal(expected, StorageControllerClassifier.Classify(controller));
    }

    [Fact]
    public void Category_FollowsThePciClassBeforeTheSetupClass()
    {
        Assert.Equal(DeviceCategory.Storage, Device([@"PCI\VEN_8086&DEV_9A0B"], [@"PCI\CC_010400"]).Category);
        Assert.Equal(DeviceCategory.Network, Device([@"PCI\VEN_8086&DEV_2723"], [@"PCI\CC_028000"]).Category);
        Assert.Equal(DeviceCategory.Display, Device([@"PCI\VEN_8086&DEV_9A49"], [@"PCI\CC_030000"], className: null).Category);
        Assert.Equal(DeviceCategory.Network, Device([@"USB\VID_0BDA&PID_8153"], className: "Net").Category);
        Assert.Equal(DeviceCategory.Other, Device([@"PCI\VEN_8086&DEV_A0A1"], [@"PCI\CC_0C0330"]).Category);
    }

    [Fact]
    public void IsWireless_UsesSubclass0x80ForPciAndTheNameForUsb()
    {
        Assert.True(Device([@"PCI\VEN_8086&DEV_2723"], [@"PCI\CC_028000"]).IsWireless);
        Assert.False(Device([@"PCI\VEN_10EC&DEV_8168"], [@"PCI\CC_020000"]).IsWireless);

        var usb = Device([@"USB\VID_0BDA&PID_B812"], className: "Net") with { Name = "Realtek 802.11ac WLAN Adapter" };
        Assert.True(usb.IsWireless);
    }

    [Theory]
    [InlineData("oem12.inf", true)]
    [InlineData("OEM3.INF", true)]
    [InlineData(@"C:\Windows\INF\oem101.inf", true)]
    [InlineData("netwtw10.inf", false)]
    [InlineData("oem.inf", false)]
    [InlineData("oemx1.inf", false)]
    [InlineData("netrtl64.inf", false)]
    public void DriverPackage_RecognisesVendorPackagesByTheirPublishedName(string inf, bool expected)
    {
        Assert.Equal(expected, new DriverPackageInfo { InfName = inf }.IsVendorPackage);
    }

    [Fact]
    public void DriverPackage_WithoutName_IsUnknown()
    {
        Assert.Null(new DriverPackageInfo().IsVendorPackage);
    }
}
