// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Writing.Windows;

/// <summary>The third-party boot components that Windows media need, embedded unchanged; see assets/third-party/SOURCES.md.</summary>
internal static class WindowsBootAssets
{
    private const string Prefix = "Bootrix.Core.Writing.Windows.";

    /// <summary>The 440 bytes of Syslinux's mbr.bin (MIT): find the active partition and run its boot sector.</summary>
    public static byte[] SyslinuxMbr() => Read("mbr.bin");

    /// <summary>The 1 MiB FAT image of the UEFI:NTFS partition.</summary>
    public static byte[] UefiNtfsImage() => Read("uefi-ntfs.img");

    private static byte[] Read(string name)
    {
        using var stream = typeof(WindowsBootAssets).Assembly.GetManifestResourceStream(Prefix + name)
            ?? throw new InvalidOperationException($"The embedded resource {name} is missing from Bootrix.Core.");
        var data = new byte[stream.Length];
        stream.ReadExactly(data);
        return data;
    }
}
