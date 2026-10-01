// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using Bootrix.Core.Errors;
using Bootrix.Core.Storage;
using Bootrix.Windows.Interop;
using Microsoft.Win32.SafeHandles;

namespace Bootrix.Windows.Storage;

public static class DiskAccess
{
    private const int OpenRetries = 40;
    private static readonly TimeSpan OpenRetryDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Opens the disk through its device interface path and confirms that the handle really points at
    /// the disk the user selected. A disk number can be handed to a different stick after a re-plug.
    /// </summary>
    public static PhysicalDisk Open(StorageDevice device, bool write, CancellationToken cancellationToken = default)
    {
        var access = write ? Kernel32.GenericRead | Kernel32.GenericWrite : Kernel32.GenericRead;
        var flags = Kernel32.FileFlagNoBuffering | (write ? Kernel32.FileFlagWriteThrough : 0);

        SafeFileHandle? handle = null;
        for (var attempt = 0; attempt < OpenRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            handle = Kernel32.CreateFile(
                device.DevicePath,
                access,
                Kernel32.FileShareRead | Kernel32.FileShareWrite,
                0,
                Kernel32.OpenExisting,
                flags,
                0);
            if (!handle.IsInvalid)
            {
                break;
            }

            var error = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
            handle.Dispose();
            handle = null;
            if (error != Kernel32.ErrorSharingViolation)
            {
                throw Translate(error, device);
            }

            Thread.Sleep(OpenRetryDelay);
        }

        if (handle is null)
        {
            throw new BootrixException(ErrorCode.DeviceBusy, device.DevicePath) { Arguments = ["?"] };
        }

        try
        {
            Verify(handle, device);
            return new PhysicalDisk(handle, device.DisplayName, device.LogicalSectorSize, device.SizeBytes);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static void Verify(SafeFileHandle handle, StorageDevice device)
    {
        var number = StorageQueries.GetDeviceNumber(handle);
        if (number is not { DeviceType: StorageQueries.DeviceTypeDisk } || (int)number.Value.Number != device.DiskNumber)
        {
            throw new BootrixException(ErrorCode.DeviceChanged, $"disk number mismatch for {device.DevicePath}");
        }

        if (device.DeviceGuid is not null
            && StorageQueries.GetDeviceGuid(handle) is { } guid
            && !string.Equals(guid.ToString("D"), device.DeviceGuid, StringComparison.OrdinalIgnoreCase))
        {
            throw new BootrixException(ErrorCode.DeviceChanged, $"device GUID mismatch for {device.DevicePath}");
        }
    }

    internal static BootrixException Translate(int win32Error, StorageDevice device) => win32Error switch
    {
        Kernel32.ErrorAccessDenied => new BootrixException(ErrorCode.DeviceBusy, $"access denied for {device.DevicePath}", new Win32Exception(win32Error))
        {
            Arguments = ["access denied"],
        },
        Kernel32.ErrorFileNotFound or Kernel32.ErrorNotReady or Kernel32.ErrorNoMediaInDevice =>
            new BootrixException(ErrorCode.DeviceNotFound, device.DevicePath, new Win32Exception(win32Error)),
        Kernel32.ErrorWriteProtect => new BootrixException(ErrorCode.DeviceWriteProtected, device.DevicePath, new Win32Exception(win32Error)),
        _ => new BootrixException(ErrorCode.DeviceNotFound, device.DevicePath, new Win32Exception(win32Error)),
    };
}
