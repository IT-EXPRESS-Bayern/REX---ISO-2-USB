// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Windows.Interop;

namespace Bootrix.Windows.Writing;

public static class VolumeFlusher
{
    /// <summary>
    /// Forces everything written through the file system of the volume out to the device. Closing the
    /// files that were copied is not enough: the data may still sit in the cache of Windows.
    /// </summary>
    public static void Flush(string volumeGuidPath)
    {
        using var handle = DeviceIo.Open(
            volumeGuidPath.TrimEnd('\\'),
            Kernel32.GenericRead | Kernel32.GenericWrite,
            Kernel32.FileShareRead | Kernel32.FileShareWrite);

        if (!Kernel32.FlushFileBuffers(handle))
        {
            throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error(), $"flushing {volumeGuidPath}");
        }
    }
}
