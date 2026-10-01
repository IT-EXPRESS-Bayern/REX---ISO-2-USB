// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;

namespace Bootrix.Windows.Interop;

internal static unsafe partial class Cfgmgr32
{
    public const uint CrSuccess = 0;

    public const int FilterTypeDeviceInterface = 0;

    public const int ActionDeviceInterfaceArrival = 0;
    public const int ActionDeviceInterfaceRemoval = 1;

    // CM_NOTIFY_FILTER: 16 bytes of header followed by a 400-byte union (the instance-id variant is the largest).
    [StructLayout(LayoutKind.Explicit, Size = 416)]
    public struct NotifyFilter
    {
        [FieldOffset(0)]
        public uint CbSize;

        [FieldOffset(4)]
        public uint Flags;

        [FieldOffset(8)]
        public int FilterType;

        [FieldOffset(12)]
        public uint Reserved;

        [FieldOffset(16)]
        public Guid ClassGuid;
    }

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Register_Notification")]
    public static partial uint RegisterNotification(
        NotifyFilter* filter,
        nint context,
        delegate* unmanaged<nint, nint, int, nint, uint, uint> callback,
        out nint notifyContext);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Unregister_Notification")]
    public static partial uint UnregisterNotification(nint notifyContext);
}
