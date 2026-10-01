// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using Bootrix.Core.Errors;
using Bootrix.Windows.Interop;
using Bootrix.Windows.Storage;
using Microsoft.Win32.SafeHandles;

namespace Bootrix.Windows.Images;

/// <summary>An ISO or VHD attached read-only; it detaches again when this object is disposed.</summary>
public sealed class MountedImage : IDisposable
{
    private readonly SafeFileHandle _handle;

    internal MountedImage(SafeFileHandle handle, string physicalPath, string? root)
    {
        _handle = handle;
        PhysicalPath = physicalPath;
        RootPath = root;
    }

    /// <summary>Device path such as \\.\CDROM1 or \\.\PhysicalDrive3.</summary>
    public string PhysicalPath { get; }

    /// <summary>Drive root such as "G:\"; null if the image has no mounted volume (e.g. a raw disk image).</summary>
    public string? RootPath { get; }

    public void Dispose()
    {
        _ = VirtDisk.DetachVirtualDisk(_handle, 0, 0);
        _handle.Dispose();
    }
}

public static unsafe class VirtualDiskMounter
{
    private static readonly TimeSpan MountTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Attaches an ISO read-only, so DISM and file copies read straight from the image without a
    /// temporary copy of several gigabytes.
    /// </summary>
    public static MountedImage MountIso(string path, CancellationToken cancellationToken = default) =>
        Mount(path, VirtDisk.DeviceIso, cancellationToken);

    public static MountedImage MountVhd(string path, CancellationToken cancellationToken = default) =>
        Mount(path, path.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase) ? VirtDisk.DeviceVhdx : VirtDisk.DeviceVhd, cancellationToken);

    private static MountedImage Mount(string path, uint deviceId, CancellationToken cancellationToken)
    {
        var type = new VirtDisk.StorageType { DeviceId = deviceId, VendorId = VirtDisk.VendorMicrosoft };
        var result = VirtDisk.OpenVirtualDisk(ref type, path, VirtDisk.AccessAttachReadOnly | VirtDisk.AccessGetInfo, 0, 0, out var handle);
        if (result != 0)
        {
            handle.Dispose();
            throw new BootrixException(ErrorCode.ImageMountFailed, $"OpenVirtualDisk {path}", new Win32Exception(result));
        }

        try
        {
            result = VirtDisk.AttachVirtualDisk(handle, 0, VirtDisk.AttachFlagReadOnly, 0, 0, 0);
            if (result != 0)
            {
                throw new BootrixException(ErrorCode.ImageMountFailed, $"AttachVirtualDisk {path}", new Win32Exception(result));
            }

            var physical = ReadPhysicalPath(handle);
            var root = deviceId == VirtDisk.DeviceIso ? WaitForIsoRoot(physical, cancellationToken) : null;
            return new MountedImage(handle, physical, root);
        }
        catch
        {
            _ = VirtDisk.DetachVirtualDisk(handle, 0, 0);
            handle.Dispose();
            throw;
        }
    }

    private static string ReadPhysicalPath(SafeFileHandle handle)
    {
        uint size = 520;
        var buffer = stackalloc char[260];
        var result = VirtDisk.GetVirtualDiskPhysicalPath(handle, ref size, buffer);
        if (result != 0)
        {
            throw new BootrixException(ErrorCode.ImageMountFailed, "GetVirtualDiskPhysicalPath", new Win32Exception(result));
        }

        return new string(buffer);
    }

    /// <summary>The attached ISO shows up as a CD-ROM; find the volume that belongs to it and wait until Windows has given it a drive letter.</summary>
    private static string WaitForIsoRoot(string physicalPath, CancellationToken cancellationToken)
    {
        var digits = new string(physicalPath.Where(char.IsDigit).ToArray());
        if (!int.TryParse(digits, out var cdRomNumber))
        {
            throw new BootrixException(ErrorCode.ImageMountFailed, $"unexpected device path {physicalPath}");
        }

        var deadline = DateTime.UtcNow + MountTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = OpticalVolumeLocator.FindDriveRoot(cdRomNumber);
            if (root is not null)
            {
                return root;
            }

            Thread.Sleep(200);
        }

        throw new BootrixException(ErrorCode.ImageMountFailed, $"no volume appeared for {physicalPath}");
    }
}
