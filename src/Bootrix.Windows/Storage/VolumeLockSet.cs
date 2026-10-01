// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Storage;
using Bootrix.Windows.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32.SafeHandles;

namespace Bootrix.Windows.Storage;

/// <summary>
/// Locks and dismounts every volume on a disk and keeps the handles open. A volume lock only holds
/// while the handle exists, so this object must live until the new file system has been written
/// and the layout has been refreshed.
/// </summary>
public sealed class VolumeLockSet : IDisposable
{
    private const int LockRetries = 40;
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(250);

    private readonly List<SafeFileHandle> _handles = [];

    private VolumeLockSet()
    {
    }

    public int VolumeCount => _handles.Count;

    public static VolumeLockSet Acquire(StorageDevice device, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        logger ??= NullLogger.Instance;
        var set = new VolumeLockSet();
        try
        {
            foreach (var volume in device.Volumes)
            {
                set._handles.Add(LockVolume(volume, logger, cancellationToken));
            }

            return set;
        }
        catch
        {
            set.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var handle in _handles)
        {
            DeviceIo.TryControl(handle, Ioctl.FsctlUnlockVolume);
            handle.Dispose();
        }

        _handles.Clear();
    }

    private static SafeFileHandle LockVolume(VolumeInfo volume, ILogger logger, CancellationToken cancellationToken)
    {
        var path = volume.VolumeGuidPath.TrimEnd('\\');
        var handle = DeviceIo.Open(
            path,
            Kernel32.GenericRead | Kernel32.GenericWrite,
            Kernel32.FileShareRead | Kernel32.FileShareWrite);

        try
        {
            // Needed to reach the last sectors of the volume; the call is allowed to fail on some file systems.
            DeviceIo.TryControl(handle, Ioctl.FsctlAllowExtendedDasdIo);

            for (var attempt = 0; attempt < LockRetries; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (DeviceIo.TryControl(handle, Ioctl.FsctlLockVolume, [], [], out _, out var error))
                {
                    if (!DeviceIo.TryControl(handle, Ioctl.FsctlDismountVolume, [], [], out _, out error))
                    {
                        logger.LogWarning("Dismount of {Volume} failed with {Error}", path, error);
                    }

                    return handle;
                }

                if (error != Kernel32.ErrorAccessDenied && error != Kernel32.ErrorSharingViolation)
                {
                    break;
                }

                Thread.Sleep(LockRetryDelay);
            }

            throw new BootrixException(ErrorCode.DeviceBusy, $"cannot lock {path}")
            {
                Arguments = [volume.DriveLetter ?? path],
            };
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }
}
