// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Windows.Interop;

namespace Bootrix.Windows.Storage;

internal static unsafe class OpticalVolumeLocator
{
    private const uint DeviceTypeCdRom = 2;

    /// <summary>Drive root ("G:\") of the volume on CD-ROM device number <paramref name="cdRomNumber"/>, or null if it has none yet.</summary>
    public static string? FindDriveRoot(int cdRomNumber)
    {
        using var errorMode = new ErrorModeScope();
        var name = stackalloc char[262];
        var find = Kernel32.FindFirstVolume(name, 262);
        if (find == -1)
        {
            return null;
        }

        try
        {
            do
            {
                var volume = new string(name);
                using var handle = DeviceIo.OpenForQuery(volume.TrimEnd('\\'));
                if (handle is null)
                {
                    continue;
                }

                var number = StorageQueries.GetDeviceNumber(handle);
                if (number is { DeviceType: DeviceTypeCdRom } && number.Value.Number == cdRomNumber)
                {
                    return FirstDriveLetter(volume);
                }
            }
            while (Kernel32.FindNextVolume(find, name, 262));
        }
        finally
        {
            Kernel32.FindVolumeClose(find);
        }

        return null;
    }

    private static string? FirstDriveLetter(string volumePath)
    {
        var buffer = new char[260];
        fixed (char* pointer = buffer)
        {
            if (!Kernel32.GetVolumePathNamesForVolumeName(volumePath, pointer, (uint)buffer.Length, out _))
            {
                return null;
            }
        }

        return VolumeCatalog.SplitMultiString(buffer).FirstOrDefault(p => p.Length == 3 && p[1] == ':');
    }
}
