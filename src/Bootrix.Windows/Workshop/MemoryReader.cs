// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Bootrix.Core.Workshop;

namespace Bootrix.Windows.Workshop;

internal static class MemoryReader
{
    private const long BytesPerKilobyte = 1024;

    public static MemoryInfo Read()
    {
        long? installed = null;
        if (FirmwareNative.GetPhysicallyInstalledSystemMemory(out var kilobytes) && kilobytes > 0)
        {
            installed = checked((long)kilobytes * BytesPerKilobyte);
        }

        long? usable = null;
        var status = new FirmwareNative.MemoryStatusEx { Length = (uint)Marshal.SizeOf<FirmwareNative.MemoryStatusEx>() };
        if (FirmwareNative.GlobalMemoryStatusEx(ref status))
        {
            usable = (long)status.TotalPhysical;
        }

        return new MemoryInfo { InstalledBytes = installed, UsableBytes = usable };
    }
}
