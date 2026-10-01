// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Planning;
using Bootrix.Core.Storage;
using Bootrix.Core.Writing;

namespace Bootrix.Core.Tests.Writing;

public class MediaPlanServiceTests
{
    private static StorageDevice Device(BusType bus, bool removable = true, bool floppy = false, int logical = 512) => new()
    {
        DiskNumber = 1,
        DevicePath = @"\\?\disk",
        Bus = bus,
        IsRemovableMedia = removable,
        IsFloppy = floppy,
        SizeBytes = 8L << 30,
        LogicalSectorSize = logical,
        PhysicalSectorSize = 4096,
    };

    [Theory]
    [InlineData(BusType.Usb, true, DeviceBus.Usb, DeviceMedium.Stick)]
    [InlineData(BusType.Usb, false, DeviceBus.Usb, DeviceMedium.Hdd)]
    [InlineData(BusType.Sd, true, DeviceBus.Sd, DeviceMedium.Card)]
    [InlineData(BusType.Mmc, true, DeviceBus.Mmc, DeviceMedium.Card)]
    [InlineData(BusType.Sata, false, DeviceBus.Sata, DeviceMedium.Hdd)]
    [InlineData(BusType.Nvme, false, DeviceBus.Nvme, DeviceMedium.Hdd)]
    [InlineData(BusType.Sas, false, DeviceBus.Scsi, DeviceMedium.Hdd)]
    [InlineData(BusType.FileBackedVirtual, false, DeviceBus.Virtual, DeviceMedium.Hdd)]
    [InlineData(BusType.Unknown, true, DeviceBus.Unknown, DeviceMedium.Stick)]
    public void CapsOf_MapsBusAndMedium(BusType bus, bool removable, DeviceBus expectedBus, DeviceMedium expectedMedium)
    {
        var caps = MediaPlanService.CapsOf(Device(bus, removable));

        Assert.Equal(expectedBus, caps.Bus);
        Assert.Equal(expectedMedium, caps.Medium);
        Assert.Equal(removable, caps.Removable);
    }

    [Fact]
    public void CapsOf_AFloppyIsAFloppyWhateverTheBusSays()
    {
        Assert.Equal(DeviceMedium.Floppy, MediaPlanService.CapsOf(Device(BusType.Usb, floppy: true)).Medium);
    }

    [Fact]
    public void CapsOf_KeepsSizesAndSectorSizes()
    {
        var caps = MediaPlanService.CapsOf(Device(BusType.Usb, logical: 4096));

        Assert.Equal(8L << 30, caps.SizeBytes);
        Assert.Equal(4096, caps.LogicalSectorSize);
        Assert.Equal(4096, caps.PhysicalSectorSize);
    }
}
