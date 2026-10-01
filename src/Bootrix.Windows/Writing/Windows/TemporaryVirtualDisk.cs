// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Globalization;
using Bootrix.Core.Errors;
using Bootrix.Windows.Interop;
using Microsoft.Win32.SafeHandles;

namespace Bootrix.Windows.Writing.Windows;

/// <summary>A VHD attached read-write without drive letters, for as long as this object lives. Windows mounts its volumes under their GUID paths.</summary>
internal sealed class TemporaryVirtualDisk : IDisposable
{
    private readonly SafeFileHandle _handle;

    private TemporaryVirtualDisk(SafeFileHandle handle, int diskNumber)
    {
        _handle = handle;
        DiskNumber = diskNumber;
    }

    public int DiskNumber { get; }

    public static unsafe TemporaryVirtualDisk Attach(string vhdPath)
    {
        var type = new VirtDisk.StorageType { DeviceId = VirtDisk.DeviceVhd, VendorId = VirtDisk.VendorMicrosoft };
        var result = VirtDisk.OpenVirtualDisk(ref type, vhdPath, VirtDisk.AccessAttachReadWrite | VirtDisk.AccessGetInfo, 0, 0, out var handle);
        if (result != 0)
        {
            handle.Dispose();
            throw new BootrixException(ErrorCode.ImageMountFailed, $"OpenVirtualDisk {vhdPath}", new Win32Exception(result));
        }

        try
        {
            result = VirtDisk.AttachVirtualDisk(handle, 0, VirtDisk.AttachFlagNoDriveLetter, 0, 0, 0);
            if (result != 0)
            {
                throw new BootrixException(ErrorCode.ImageMountFailed, $"AttachVirtualDisk {vhdPath}", new Win32Exception(result));
            }

            uint size = 520;
            var buffer = stackalloc char[260];
            result = VirtDisk.GetVirtualDiskPhysicalPath(handle, ref size, buffer);
            if (result != 0)
            {
                throw new BootrixException(ErrorCode.ImageMountFailed, "GetVirtualDiskPhysicalPath", new Win32Exception(result));
            }

            // "\\.\PhysicalDrive7"
            var path = new string(buffer);
            var digits = new string(path.Where(char.IsDigit).ToArray());
            if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                throw new BootrixException(ErrorCode.ImageMountFailed, $"unexpected device path {path}");
            }

            return new TemporaryVirtualDisk(handle, number);
        }
        catch
        {
            _ = VirtDisk.DetachVirtualDisk(handle, 0, 0);
            handle.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _ = VirtDisk.DetachVirtualDisk(_handle, 0, 0);
        _handle.Dispose();
    }
}
