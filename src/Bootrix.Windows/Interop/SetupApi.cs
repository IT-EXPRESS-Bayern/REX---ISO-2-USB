// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Bootrix.Windows.Interop;

internal static unsafe partial class SetupApi
{
    public static readonly Guid DiskInterfaceGuid = new("53f56307-b6bf-11d0-94f2-00a0c91efb8b");
    public static readonly Guid VolumeInterfaceGuid = new("53f5630d-b6bf-11d0-94f2-00a0c91efb8b");
    public static readonly Guid CdRomInterfaceGuid = new("53f56308-b6bf-11d0-94f2-00a0c91efb8b");
    public static readonly Guid FloppyInterfaceGuid = new("53f56311-b6bf-11d0-94f2-00a0c91efb8b");

    private const uint DigcfPresent = 0x2;
    private const uint DigcfDeviceInterface = 0x10;
    private const int ErrorNoMoreItems = 259;

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public uint CbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public nuint Reserved;
    }

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true)]
    private static partial nint SetupDiGetClassDevs(in Guid classGuid, nint enumerator, nint hwndParent, uint flags);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiEnumDeviceInterfaces", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiEnumDeviceInterfaces(nint deviceInfoSet, nint deviceInfoData, in Guid interfaceClassGuid, uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceInterfaceDetail(nint deviceInfoSet, in SpDeviceInterfaceData deviceInterfaceData, byte* detailData, uint detailDataSize, out uint requiredSize, nint deviceInfoData);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);

    /// <summary>Device interface paths (\\?\...) of all present devices of an interface class.</summary>
    public static IReadOnlyList<string> EnumerateInterfacePaths(Guid interfaceClass)
    {
        var set = SetupDiGetClassDevs(interfaceClass, 0, 0, DigcfPresent | DigcfDeviceInterface);
        if (set == -1 || set == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var paths = new List<string>();
        try
        {
            for (uint index = 0; ; index++)
            {
                var data = new SpDeviceInterfaceData { CbSize = (uint)sizeof(SpDeviceInterfaceData) };
                if (!SetupDiEnumDeviceInterfaces(set, 0, interfaceClass, index, ref data))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error == ErrorNoMoreItems)
                    {
                        break;
                    }

                    throw new Win32Exception(error);
                }

                var path = ReadDevicePath(set, data);
                if (path is not null)
                {
                    paths.Add(path);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return paths;
    }

    private static string? ReadDevicePath(nint set, SpDeviceInterfaceData data)
    {
        SetupDiGetDeviceInterfaceDetail(set, data, null, 0, out var required, 0);
        if (required < 6)
        {
            return null;
        }

        var buffer = new byte[required];
        fixed (byte* pointer = buffer)
        {
            // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W is 8 on 64-bit and 6 on 32-bit, not the size of the buffer.
            *(uint*)pointer = nint.Size == 8 ? 8u : 6u;
            if (!SetupDiGetDeviceInterfaceDetail(set, data, pointer, required, out _, 0))
            {
                return null;
            }

            return new string((char*)(pointer + 4));
        }
    }
}
