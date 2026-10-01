// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Bootrix.Windows.Interop;

internal static unsafe partial class TokenInformation
{
    /// <summary>TOKEN_INFORMATION_CLASS value of TokenElevation.</summary>
    public const int TokenElevation = 20;

    [StructLayout(LayoutKind.Sequential)]
    public struct Elevation
    {
        public uint TokenIsElevated;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, void* information, uint informationLength, out uint returnLength);
}
