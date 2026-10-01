// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Bootrix.Windows.Interop;

/// <summary>A handle that is closed with CloseHandle, such as the one OpenProcess returns.</summary>
internal sealed class SafeKernelObjectHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeKernelObjectHandle()
        : base(ownsHandle: true)
    {
    }

    protected override bool ReleaseHandle() => ProcessApi.CloseHandle(handle);
}

internal static unsafe partial class ProcessApi
{
    /// <summary>Enough to read the image path of a process, and allowed for processes of other users and higher integrity levels.</summary>
    public const uint ProcessQueryLimitedInformation = 0x1000;

    private const int MaxImagePathChars = 32_768;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial SafeKernelObjectHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageName(SafeKernelObjectHandle process, uint flags, char* exeName, ref uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    /// <summary>The Win32 path of the program a process runs, or null when the process is gone or cannot be inspected.</summary>
    public static string? GetImagePath(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        using var process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (process.IsInvalid)
        {
            return null;
        }

        var buffer = new char[MaxImagePathChars];
        var size = (uint)buffer.Length;
        fixed (char* path = buffer)
        {
            return QueryFullProcessImageName(process, 0, path, ref size) ? new string(path, 0, (int)size) : null;
        }
    }
}
