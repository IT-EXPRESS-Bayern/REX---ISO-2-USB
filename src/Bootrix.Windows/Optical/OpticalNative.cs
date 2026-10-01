// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Bootrix.Windows.Optical;

internal static unsafe partial class OpticalNative
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetFilePointerEx(SafeFileHandle file, long distanceToMove, out long newFilePointer, uint moveMethod);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReadFile(SafeFileHandle file, byte* buffer, uint bytesToRead, out uint bytesRead, nint overlapped);
}
