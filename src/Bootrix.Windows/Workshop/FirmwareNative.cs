// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;

namespace Bootrix.Windows.Workshop;

internal static unsafe partial class FirmwareNative
{
    public const uint FirmwareTypeBios = 1;
    public const uint FirmwareTypeUefi = 2;

    public const int ErrorInvalidFunction = 1;
    public const int ErrorInvalidParameter = 87;
    public const int ErrorInsufficientBuffer = 122;
    public const int ErrorEnvVarNotFound = 203;
    public const int ErrorNotFound = 1168;

    /// <summary>The 64-byte structure GlobalMemoryStatusEx fills in.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFirmwareType(out uint firmwareType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint GetSystemFirmwareTable(uint providerSignature, uint tableId, byte* buffer, uint bufferSize);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFirmwareEnvironmentVariableW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint GetFirmwareEnvironmentVariable(string name, string guid, byte* buffer, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetPhysicallyInstalledSystemMemory(out ulong totalMemoryInKilobytes);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetOEMCP();
}
