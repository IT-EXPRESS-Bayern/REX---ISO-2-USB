// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Bootrix.Core.Storage;
using Bootrix.Windows.Interop;

namespace Bootrix.Windows.Storage;

/// <summary>Every volume Windows knows, with the disk extents it lives on. This is the only reliable link between drive letters and physical disks.</summary>
internal static unsafe class VolumeCatalog
{
    public static List<VolumeEntry> Load()
    {
        using var errorMode = new ErrorModeScope();
        var result = new List<VolumeEntry>();
        var name = stackalloc char[262];
        var find = Kernel32.FindFirstVolume(name, 262);
        if (find == -1)
        {
            return result;
        }

        try
        {
            do
            {
                result.Add(Describe(new string(name)));
            }
            while (Kernel32.FindNextVolume(find, name, 262));
        }
        finally
        {
            Kernel32.FindVolumeClose(find);
        }

        return result;
    }

    private static VolumeEntry Describe(string volumePath)
    {
        var extents = ReadExtents(volumePath);
        var mountPoints = ReadMountPoints(volumePath);
        var (label, fileSystem, readable) = ReadInformation(volumePath);

        long total = 0;
        long free = 0;
        if (readable && Kernel32.GetDiskFreeSpaceEx(volumePath, out var freeAvailable, out var totalBytes, out _))
        {
            total = (long)totalBytes;
            free = (long)freeAvailable;
        }

        var info = new VolumeInfo
        {
            VolumeGuidPath = volumePath,
            MountPoints = mountPoints,
            Label = label,
            FileSystem = fileSystem,
            TotalBytes = total,
            FreeBytes = free,
            Extents = extents,
            Unreadable = !readable && extents.Count > 0,
        };

        return new VolumeEntry(info, QueryNtName(volumePath));
    }

    private static List<DiskExtent> ReadExtents(string volumePath)
    {
        // The device path of a volume has no trailing backslash; with it, CreateFile opens the root directory instead.
        using var handle = DeviceIo.OpenForQuery(volumePath.TrimEnd('\\'));
        if (handle is null)
        {
            return [];
        }

        var data = DeviceIo.QueryGrowing(handle, Ioctl.VolumeGetVolumeDiskExtents, [], 256);
        if (data is null || data.Length < 8)
        {
            return [];
        }

        var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(data);
        var extents = new List<DiskExtent>(count);
        for (var i = 0; i < count && 8 + (i + 1) * 24 <= data.Length; i++)
        {
            var offset = 8 + i * 24;
            extents.Add(new DiskExtent(
                (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset)),
                BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(offset + 8)),
                BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(offset + 16))));
        }

        return extents;
    }

    private static List<string> ReadMountPoints(string volumePath)
    {
        var size = 260u;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var buffer = new char[size];
            fixed (char* pointer = buffer)
            {
                if (Kernel32.GetVolumePathNamesForVolumeName(volumePath, pointer, size, out var needed))
                {
                    return SplitMultiString(buffer);
                }

                if (Marshal.GetLastPInvokeError() != Kernel32.ErrorMoreData)
                {
                    return [];
                }

                size = needed;
            }
        }

        return [];
    }

    private static (string? Label, string? FileSystem, bool Readable) ReadInformation(string volumePath)
    {
        var label = stackalloc char[262];
        var fileSystem = stackalloc char[262];
        if (!Kernel32.GetVolumeInformation(volumePath, label, 262, out _, out _, out _, fileSystem, 262))
        {
            return (null, null, false);
        }

        var labelText = new string(label);
        return (labelText.Length == 0 ? null : labelText, new string(fileSystem), true);
    }

    private static string? QueryNtName(string volumePath)
    {
        // "\\?\Volume{guid}\" -> "Volume{guid}"
        var dosName = volumePath.TrimEnd('\\');
        if (dosName.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            dosName = dosName[4..];
        }

        var target = stackalloc char[520];
        var length = Kernel32.QueryDosDevice(dosName, target, 520);
        return length == 0 ? null : new string(target);
    }

    internal static List<string> SplitMultiString(char[] buffer)
    {
        var result = new List<string>();
        var start = 0;
        for (var i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] != '\0')
            {
                continue;
            }

            if (i == start)
            {
                break;
            }

            result.Add(new string(buffer, start, i - start));
            start = i + 1;
        }

        return result;
    }
}

internal sealed record VolumeEntry(VolumeInfo Info, string? NtDeviceName);
