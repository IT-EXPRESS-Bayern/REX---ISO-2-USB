// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Storage;
using Bootrix.Windows.Interop;
using Microsoft.Win32;

namespace Bootrix.Windows.Storage;

/// <summary>
/// Finds the disks that must never be touched: the one holding the running Windows, the one with
/// the boot files and every disk that carries a pagefile. This is the last line of defence against
/// wiping the wrong drive, so it errs on the side of excluding too much.
/// </summary>
internal static unsafe class SystemDiskDetector
{
    public static Dictionary<int, DeviceProtection> Detect(IReadOnlyList<VolumeEntry> volumes)
    {
        var result = new Dictionary<int, DeviceProtection>();

        void Mark(IEnumerable<VolumeEntry> entries, DeviceProtection flag)
        {
            foreach (var disk in entries.SelectMany(e => e.Info.Extents).Select(e => e.DiskNumber))
            {
                result[disk] = result.GetValueOrDefault(disk) | flag;
            }
        }

        var windowsRoot = Path.GetPathRoot(WindowsDirectory())?.ToUpperInvariant();
        if (windowsRoot is not null)
        {
            Mark(volumes.Where(v => HasMountPoint(v, windowsRoot)), DeviceProtection.BootDisk | DeviceProtection.SystemDisk);
        }

        var systemPartition = ReadSystemPartitionName();
        if (systemPartition is not null)
        {
            Mark(volumes.Where(v => string.Equals(v.NtDeviceName, systemPartition, StringComparison.OrdinalIgnoreCase)), DeviceProtection.SystemDisk);
        }

        foreach (var root in PagefileRoots())
        {
            Mark(volumes.Where(v => HasMountPoint(v, root)), DeviceProtection.PagefileDisk);
        }

        return result;
    }

    private static bool HasMountPoint(VolumeEntry volume, string root) =>
        volume.Info.MountPoints.Any(m => string.Equals(m, root, StringComparison.OrdinalIgnoreCase));

    private static string WindowsDirectory()
    {
        var buffer = stackalloc char[260];
        var length = Kernel32.GetWindowsDirectory(buffer, 260);
        return length == 0 ? Environment.GetFolderPath(Environment.SpecialFolder.Windows) : new string(buffer, 0, (int)length);
    }

    /// <summary>The EFI system or boot partition recorded by setup, e.g. \Device\HarddiskVolume1.</summary>
    private static string? ReadSystemPartitionName()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\Setup");
        return key?.GetValue("SystemPartition") as string;
    }

    private static IEnumerable<string> PagefileRoots()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management");
        if (key?.GetValue("ExistingPageFiles") is not string[] files)
        {
            yield break;
        }

        foreach (var file in files)
        {
            // "\??\C:\pagefile.sys"
            var path = file.StartsWith(@"\??\", StringComparison.Ordinal) ? file[4..] : file;
            var root = Path.GetPathRoot(path);
            if (!string.IsNullOrEmpty(root))
            {
                yield return root.ToUpperInvariant();
            }
        }
    }
}
