// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using Bootrix.Core.Storage;

namespace Bootrix.Windows.Platform;

/// <summary>One writer per disk across the GUI, the CLI and a second Bootrix instance.</summary>
public sealed class DiskMutex : IDisposable
{
    private readonly Mutex _mutex;

    private DiskMutex(Mutex mutex) => _mutex = mutex;

    public static DiskMutex? TryAcquire(StorageDevice device)
    {
        var key = string.Join('|', device.DeviceGuid, device.Serial, device.SizeBytes, device.DiskNumber);
        var name = "Global\\Bootrix.Disk." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24];
        var mutex = new Mutex(false, name);
        try
        {
            if (mutex.WaitOne(0))
            {
                return new DiskMutex(mutex);
            }
        }
        catch (AbandonedMutexException)
        {
            // The previous owner crashed; the disk itself is free.
            return new DiskMutex(mutex);
        }

        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
