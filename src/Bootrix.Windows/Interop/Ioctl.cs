// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Windows.Interop;

/// <summary>Control codes built the same way as the CTL_CODE macro in winioctl.h.</summary>
internal static class Ioctl
{
    private const uint MethodBuffered = 0;
    private const uint MethodNeither = 3;
    private const uint AnyAccess = 0;
    private const uint ReadAccess = 1;
    private const uint WriteAccess = 2;

    private const uint DeviceTypeDisk = 0x07;
    private const uint DeviceTypeFileSystem = 0x09;
    private const uint DeviceTypeMountManager = 0x6D;
    private const uint DeviceTypeStorage = 0x2D;
    private const uint DeviceTypeVolume = 0x56;

    public static uint CtlCode(uint deviceType, uint function, uint method, uint access) =>
        (deviceType << 16) | (access << 14) | (function << 2) | method;

    public static readonly uint DiskGetDriveGeometryEx = CtlCode(DeviceTypeDisk, 0x28, MethodBuffered, AnyAccess);
    public static readonly uint DiskGetDriveLayoutEx = CtlCode(DeviceTypeDisk, 0x14, MethodBuffered, AnyAccess);
    public static readonly uint DiskSetDriveLayoutEx = CtlCode(DeviceTypeDisk, 0x15, MethodBuffered, ReadAccess | WriteAccess);
    public static readonly uint DiskCreateDisk = CtlCode(DeviceTypeDisk, 0x16, MethodBuffered, ReadAccess | WriteAccess);
    public static readonly uint DiskDeleteDriveLayout = CtlCode(DeviceTypeDisk, 0x40, MethodBuffered, ReadAccess | WriteAccess);
    public static readonly uint DiskUpdateProperties = CtlCode(DeviceTypeDisk, 0x50, MethodBuffered, AnyAccess);
    public static readonly uint DiskGetLengthInfo = CtlCode(DeviceTypeDisk, 0x17, MethodBuffered, ReadAccess);
    public static readonly uint DiskIsWritable = CtlCode(DeviceTypeDisk, 0x09, MethodBuffered, AnyAccess);
    public static readonly uint DiskGetDiskAttributes = CtlCode(DeviceTypeDisk, 0x3C, MethodBuffered, AnyAccess);
    public static readonly uint DiskSetDiskAttributes = CtlCode(DeviceTypeDisk, 0x3D, MethodBuffered, ReadAccess | WriteAccess);

    public static readonly uint StorageGetDeviceNumber = CtlCode(DeviceTypeStorage, 0x420, MethodBuffered, AnyAccess);
    public static readonly uint StorageGetDeviceNumberEx = CtlCode(DeviceTypeStorage, 0x421, MethodBuffered, AnyAccess);
    public static readonly uint StorageQueryProperty = CtlCode(DeviceTypeStorage, 0x500, MethodBuffered, AnyAccess);
    public static readonly uint StorageCheckVerify = CtlCode(DeviceTypeStorage, 0x200, MethodBuffered, ReadAccess);
    public static readonly uint StorageCheckVerify2 = CtlCode(DeviceTypeStorage, 0x200, MethodBuffered, AnyAccess);
    public static readonly uint StorageEjectMedia = CtlCode(DeviceTypeStorage, 0x202, MethodBuffered, ReadAccess);
    public static readonly uint StorageMediaRemoval = CtlCode(DeviceTypeStorage, 0x201, MethodBuffered, ReadAccess);

    public static readonly uint VolumeGetVolumeDiskExtents = CtlCode(DeviceTypeVolume, 0, MethodBuffered, AnyAccess);

    public static readonly uint FsctlLockVolume = CtlCode(DeviceTypeFileSystem, 6, MethodBuffered, AnyAccess);
    public static readonly uint FsctlUnlockVolume = CtlCode(DeviceTypeFileSystem, 7, MethodBuffered, AnyAccess);
    public static readonly uint FsctlDismountVolume = CtlCode(DeviceTypeFileSystem, 8, MethodBuffered, AnyAccess);
    public static readonly uint FsctlAllowExtendedDasdIo = CtlCode(DeviceTypeFileSystem, 32, MethodNeither, AnyAccess);
    public static readonly uint FsctlGetVolumeBitmap = CtlCode(DeviceTypeFileSystem, 27, MethodNeither, AnyAccess);

    public static readonly uint MountMgrQueryAutoMount = CtlCode(DeviceTypeMountManager, 15, MethodBuffered, AnyAccess);
    public static readonly uint MountMgrSetAutoMount = CtlCode(DeviceTypeMountManager, 16, MethodBuffered, ReadAccess | WriteAccess);
}
