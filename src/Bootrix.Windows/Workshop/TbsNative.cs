// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;

namespace Bootrix.Windows.Workshop;

/// <summary>TPM Base Services (tbs.dll). The library is missing in some Windows PE images, which callers must expect.</summary>
internal static partial class TbsNative
{
    public const uint Success = 0;

    /// <summary>TBS_E_TPM_NOT_FOUND: Windows sees no TPM, whether it is absent or switched off in the firmware.</summary>
    public const uint TpmNotFound = 0x8028400F;

    public const uint TpmVersion12 = 1;
    public const uint TpmVersion20 = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceInfo
    {
        public uint StructVersion;
        public uint TpmVersion;
        public uint TpmInterfaceType;
        public uint TpmImplementationRevision;
    }

    // tbs.dll is not a known DLL, so the search is limited to System32 explicitly.
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("tbs.dll", EntryPoint = "Tbsi_GetDeviceInfo")]
    public static partial uint GetDeviceInfo(uint size, out DeviceInfo info);
}
