// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Bootrix.Windows.Interop;

internal static partial class VirtDisk
{
    public const uint DeviceIso = 1;
    public const uint DeviceVhd = 2;
    public const uint DeviceVhdx = 3;

    public const uint AccessAttachReadOnly = 0x00010000;
    public const uint AccessGetInfo = 0x00080000;

    public const uint AttachFlagReadOnly = 0x1;

    public static readonly Guid VendorMicrosoft = new("ec984aec-a0f9-47e9-901f-71415a66345b");

    [StructLayout(LayoutKind.Sequential)]
    public struct StorageType
    {
        public uint DeviceId;
        public Guid VendorId;
    }

    [LibraryImport("virtdisk.dll", EntryPoint = "OpenVirtualDisk", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int OpenVirtualDisk(ref StorageType storageType, string path, uint accessMask, uint flags, nint parameters, out SafeFileHandle handle);

    [LibraryImport("virtdisk.dll", EntryPoint = "AttachVirtualDisk")]
    public static partial int AttachVirtualDisk(SafeFileHandle handle, nint securityDescriptor, uint flags, uint providerFlags, nint parameters, nint overlapped);

    [LibraryImport("virtdisk.dll", EntryPoint = "DetachVirtualDisk")]
    public static partial int DetachVirtualDisk(SafeFileHandle handle, uint flags, uint providerFlags);

    [LibraryImport("virtdisk.dll", EntryPoint = "GetVirtualDiskPhysicalPath")]
    public static unsafe partial int GetVirtualDiskPhysicalPath(SafeFileHandle handle, ref uint sizeInBytes, char* path);
}
