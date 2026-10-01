// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Windows.Interop;

namespace Bootrix.Windows.Tests.Interop;

public class IoctlTests
{
    // Values taken from winioctl.h / ntddstor.h; a wrong CTL_CODE would silently talk to the wrong driver function.
    [Theory]
    [InlineData(nameof(Ioctl.DiskGetDriveGeometryEx), 0x000700A0u)]
    [InlineData(nameof(Ioctl.DiskGetDriveLayoutEx), 0x00070050u)]
    [InlineData(nameof(Ioctl.DiskSetDriveLayoutEx), 0x0007C054u)]
    [InlineData(nameof(Ioctl.DiskCreateDisk), 0x0007C058u)]
    [InlineData(nameof(Ioctl.DiskDeleteDriveLayout), 0x0007C100u)]
    [InlineData(nameof(Ioctl.DiskUpdateProperties), 0x00070140u)]
    [InlineData(nameof(Ioctl.DiskGetLengthInfo), 0x0007405Cu)]
    [InlineData(nameof(Ioctl.StorageGetDeviceNumber), 0x002D1080u)]
    [InlineData(nameof(Ioctl.StorageQueryProperty), 0x002D1400u)]
    [InlineData(nameof(Ioctl.StorageCheckVerify), 0x002D4800u)]
    [InlineData(nameof(Ioctl.StorageCheckVerify2), 0x002D0800u)]
    [InlineData(nameof(Ioctl.StorageEjectMedia), 0x002D4808u)]
    [InlineData(nameof(Ioctl.VolumeGetVolumeDiskExtents), 0x00560000u)]
    [InlineData(nameof(Ioctl.FsctlLockVolume), 0x00090018u)]
    [InlineData(nameof(Ioctl.FsctlUnlockVolume), 0x0009001Cu)]
    [InlineData(nameof(Ioctl.FsctlDismountVolume), 0x00090020u)]
    [InlineData(nameof(Ioctl.FsctlAllowExtendedDasdIo), 0x00090083u)]
    [InlineData(nameof(Ioctl.MountMgrQueryAutoMount), 0x006D003Cu)]
    [InlineData(nameof(Ioctl.MountMgrSetAutoMount), 0x006DC040u)]
    public void ControlCodesMatchTheSdk(string field, uint expected)
    {
        var actual = (uint)typeof(Ioctl).GetField(field)!.GetValue(null)!;

        Assert.Equal(expected, actual);
    }
}
