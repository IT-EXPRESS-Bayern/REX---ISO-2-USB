// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Workshop.Hardware;

namespace Bootrix.Core.Tests.Workshop;

public class PciIdTests
{
    // Hardware and compatible IDs of a Tiger Lake VMD controller in the forms Windows lists them (device ID strings for PCI devices).
    private static readonly string[] VmdHardwareIds =
    [
        @"PCI\VEN_8086&DEV_9A0B&SUBSYS_0A5A1028&REV_01",
        @"PCI\VEN_8086&DEV_9A0B&SUBSYS_0A5A1028",
        @"PCI\VEN_8086&DEV_9A0B&REV_01",
        @"PCI\VEN_8086&DEV_9A0B",
    ];

    private static readonly string[] VmdCompatibleIds =
    [
        @"PCI\VEN_8086&DEV_9A0B&REV_01",
        @"PCI\VEN_8086&DEV_9A0B",
        @"PCI\VEN_8086&CC_010400",
        @"PCI\VEN_8086&CC_0104",
        @"PCI\VEN_8086",
        @"PCI\CC_010400",
        @"PCI\CC_0104",
    ];

    [Fact]
    public void From_CombinesHardwareAndCompatibleIds()
    {
        var id = PciId.From(VmdHardwareIds, VmdCompatibleIds);

        Assert.NotNull(id);
        Assert.Equal((ushort)0x8086, id.Value.VendorId);
        Assert.Equal((ushort)0x9A0B, id.Value.DeviceId);
        Assert.Equal(0x0A5A1028u, id.Value.SubsystemId);
        Assert.Equal((byte)0x01, id.Value.Revision);
        Assert.Equal(new PciClassCode(0x01, 0x04, 0x00), id.Value.Class);
        Assert.Equal(@"PCI\VEN_8086&DEV_9A0B", id.Value.HardwareId);
        Assert.Equal("8086:9A0B", id.Value.ToString());
    }

    [Fact]
    public void From_ShortClassForm_HasNoProgrammingInterface()
    {
        var id = PciId.From([@"PCI\VEN_10EC&DEV_8168"], [@"PCI\CC_0200"]);

        Assert.Equal(new PciClassCode(0x02, 0x00), id!.Value.Class);
        Assert.Null(id.Value.Class!.Value.ProgrammingInterface);
    }

    [Fact]
    public void From_PrefersTheSixDigitClassOverTheFourDigitOne()
    {
        var id = PciId.From([@"PCI\VEN_8086&DEV_A103"], [@"PCI\CC_0104", @"PCI\CC_010400"]);

        Assert.Equal(new PciClassCode(0x01, 0x04, 0x00), id!.Value.Class);
    }

    [Theory]
    [InlineData(@"pci\ven_8086&dev_9a0b", 0x8086, 0x9A0B)]
    [InlineData(@"PCI\VEN_1AF4&DEV_1042&SUBSYS_11001AF4&REV_01", 0x1AF4, 0x1042)]
    public void From_IsCaseInsensitive(string hardwareId, int vendor, int device)
    {
        var id = PciId.From([hardwareId]);

        Assert.Equal((ushort)vendor, id!.Value.VendorId);
        Assert.Equal((ushort)device, id.Value.DeviceId);
    }

    [Theory]
    [InlineData(@"USB\VID_0BDA&PID_8153")]
    [InlineData(@"ACPI\PNP0A08")]
    [InlineData(@"PCI\CC_0104")]
    [InlineData(@"PCI\VEN_8086")]
    [InlineData(@"PCI\VEN_80&DEV_9A0B")]
    [InlineData(@"PCI\VEN_ZZZZ&DEV_9A0B")]
    [InlineData("")]
    public void From_NonPciOrIncompleteIds_ReturnsNull(string hardwareId)
    {
        Assert.Null(PciId.From([hardwareId]));
    }

    [Fact]
    public void From_NoIds_ReturnsNull()
    {
        Assert.Null(PciId.From([]));
    }

    [Fact]
    public void PciClassCode_ToString_UsesWindowsNotation()
    {
        Assert.Equal("010400", new PciClassCode(1, 4, 0).ToString());
        Assert.Equal("0280", new PciClassCode(2, 0x80).ToString());
    }

    [Fact]
    public void Tables_HaveSourcesAndUniqueIds()
    {
        foreach (var table in new[] { StorageControllerTable.IntelVmd, StorageControllerTable.IntelRstRaid, StorageControllerTable.VirtualStorage, StorageControllerTable.VirtualNetwork })
        {
            Assert.False(string.IsNullOrWhiteSpace(table.Source), table.Description);
            Assert.False(string.IsNullOrWhiteSpace(table.Description));
            Assert.NotEmpty(table.Entries);
            Assert.Equal(table.Entries.Count, table.Entries.Select(e => (e.VendorId, e.DeviceId)).Distinct().Count());
            Assert.All(table.Entries, e => Assert.False(string.IsNullOrWhiteSpace(e.Name)));
        }
    }

    [Fact]
    public void Tables_DoNotOverlap()
    {
        var vmd = StorageControllerTable.IntelVmd.Entries.Select(e => (e.VendorId, e.DeviceId));
        var raid = StorageControllerTable.IntelRstRaid.Entries.Select(e => (e.VendorId, e.DeviceId));

        Assert.Empty(vmd.Intersect(raid));
    }

    [Theory]
    [InlineData(0x9A0B, "Tiger Lake")]
    [InlineData(0x467F, "Alder Lake")]
    [InlineData(0xA77F, "Raptor Lake")]
    [InlineData(0x4C3D, "Rocket Lake")]
    [InlineData(0x7D0B, "Meteor Lake")]
    [InlineData(0xAD0B, "Arrow Lake")]
    public void IntelVmd_ContainsTheClientGenerationsKnownFromTheLinuxDriver(int device, string generation)
    {
        var id = new PciId(0x8086, (ushort)device);

        Assert.True(StorageControllerTable.IntelVmd.Contains(id), generation);
        Assert.NotNull(StorageControllerTable.IntelVmd.Find(id));
    }

    [Theory]
    [InlineData(0x2822)]
    [InlineData(0xA105)]
    [InlineData(0xA106)]
    [InlineData(0x1C04)]
    [InlineData(0x8C04)]
    [InlineData(0x43D6)]
    [InlineData(0x06D7)]
    public void IntelRstRaid_ContainsRaidModeSataControllers(int device)
    {
        Assert.True(StorageControllerTable.IntelRstRaid.Contains(new PciId(0x8086, (ushort)device)));
    }

    [Theory]
    [InlineData(0xA102)]
    [InlineData(0xA1D2)]
    [InlineData(0xA252)]
    [InlineData(0x2821)]
    [InlineData(0x1C02)]
    public void IntelRstRaid_DoesNotContainAhciModeControllers(int device)
    {
        Assert.False(StorageControllerTable.IntelRstRaid.Contains(new PciId(0x8086, (ushort)device)));
    }

    [Fact]
    public void Tables_MatchOnVendorToo()
    {
        Assert.False(StorageControllerTable.IntelVmd.Contains(new PciId(0x1022, 0x9A0B)));
    }

    [Fact]
    public void PciVendors_NamesTheCommonVendors()
    {
        Assert.Equal("Intel", PciVendors.NameOf(0x8086));
        Assert.Equal("Realtek", PciVendors.NameOf(0x10EC));
        Assert.Null(PciVendors.NameOf(0xBEEF));
    }
}
