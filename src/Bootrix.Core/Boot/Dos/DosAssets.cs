// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Boot.Dos;

/// <summary>The FreeDOS programs and boot code embedded from assets/third-party; sources and licenses are listed in SOURCES.md there.</summary>
internal static class DosAssets
{
    private const string FreeDosPrefix = "Bootrix.Core.Assets.FreeDos.";
    private const string MbrPrefix = "Bootrix.Core.Boot.Syslinux.mbr/";

    public static byte[] FreeDos(string fileName) => Read(FreeDosPrefix + fileName);

    public static byte[] Mbr(string fileName) => Read(MbrPrefix + fileName);

    private static byte[] Read(string resourceName)
    {
        using var stream = typeof(DosAssets).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"The embedded file {resourceName} is missing from Bootrix.Core.");
        var data = new byte[stream.Length];
        stream.ReadExactly(data);
        return data;
    }
}
