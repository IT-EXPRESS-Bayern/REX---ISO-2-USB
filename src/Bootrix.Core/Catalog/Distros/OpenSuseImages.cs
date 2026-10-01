// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Catalog.Distros.Common;

namespace Bootrix.Core.Catalog.Distros;

/// <param name="Directory">Path below download.opensuse.org, ending with a slash.</param>
/// <param name="FileTemplate">File name with <c>{arch}</c> where the architecture goes ("x86_64", "aarch64").</param>
/// <param name="Digest">Which checksum file is published next to the image: "sha256" or "sha512".</param>
internal sealed record OpenSuseImage(
    string Id,
    string Name,
    string Directory,
    string FileTemplate,
    string Digest,
    IReadOnlyList<string> Architectures);

/// <summary>
/// Where openSUSE keeps each image. The naming changed with Leap 16 (installer images in <c>offline/</c>, SHA-512
/// checksums); Leap 15 and Tumbleweed still use the classic "Media" and "Current" names with SHA-256.
/// </summary>
internal static class OpenSuseImages
{
    private static readonly string[] PcAndArm = [Architectures.X64, Architectures.Arm64];
    private static readonly string[] PcOnly = [Architectures.X64];

    public static IReadOnlyList<OpenSuseImage> Leap(NumericVersion version)
    {
        var name = $"openSUSE Leap {version}";
        if (version.Major >= 16)
        {
            var directory = $"distribution/leap/{version}/offline/";
            return
            [
                new($"{version}/offline", $"{name} (offline installer)", directory, $"Leap-{version}-offline-installer-{{arch}}.install.iso", "sha512", PcAndArm),
                new($"{version}/online", $"{name} (online installer)", directory, $"Leap-{version}-online-installer-{{arch}}.install.iso", "sha512", PcAndArm),
            ];
        }

        var isoDirectory = $"distribution/leap/{version}/iso/";
        return
        [
            new($"{version}/dvd", $"{name} (DVD)", isoDirectory, $"openSUSE-Leap-{version}-DVD-{{arch}}-Media.iso", "sha256", PcAndArm),
            new($"{version}/net", $"{name} (network installer)", isoDirectory, $"openSUSE-Leap-{version}-NET-{{arch}}-Media.iso", "sha256", PcAndArm),
        ];
    }

    /// <summary>The rolling release always sits behind "...-Current.iso", a link to the newest snapshot that passed testing.</summary>
    public static IReadOnlyList<OpenSuseImage> Tumbleweed() =>
    [
        Rolling("dvd", "DVD", "DVD"),
        Rolling("net", "network installer", "NET"),
        Rolling("kde-live", "KDE Plasma live", "KDE-Live"),
        Rolling("gnome-live", "GNOME live", "GNOME-Live"),
        Rolling("xfce-live", "Xfce live", "XFCE-Live"),
    ];

    private static OpenSuseImage Rolling(string id, string description, string fileKind) =>
        new($"tumbleweed/{id}", $"openSUSE Tumbleweed ({description})", "tumbleweed/iso/", $"openSUSE-Tumbleweed-{fileKind}-{{arch}}-Current.iso", "sha256", PcOnly);
}
