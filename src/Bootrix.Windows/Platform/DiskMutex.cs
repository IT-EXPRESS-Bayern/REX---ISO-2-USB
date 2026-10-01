// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using Bootrix.Core.Storage;

namespace Bootrix.Windows.Platform;

/// <summary>
/// One writer per disk across the GUI, the CLI and a second Bootrix instance. A named semaphore is used
/// instead of a mutex: a mutex belongs to the thread that took it, and a job takes the lock in one
/// async step and gives it back in the cleanup, which usually runs on another thread. The kernel drops a
/// semaphore with its last handle, so a crashed process cannot leave the disk locked for good.
/// </summary>
public sealed class DiskMutex : IDisposable
{
    private readonly Semaphore _semaphore;
    private int _released;

    private DiskMutex(Semaphore semaphore) => _semaphore = semaphore;

    public static DiskMutex? TryAcquire(StorageDevice device)
    {
        var key = string.Join('|', device.DeviceGuid, device.Serial, device.SizeBytes, device.DiskNumber);
        var name = "Global\\Bootrix.Disk." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24];
        var semaphore = new Semaphore(1, 1, name);
        if (semaphore.WaitOne(0))
        {
            return new DiskMutex(semaphore);
        }

        semaphore.Dispose();
        return null;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0)
        {
            _semaphore.Release();
            _semaphore.Dispose();
        }
    }
}
