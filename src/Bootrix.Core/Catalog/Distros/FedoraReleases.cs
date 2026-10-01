// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bootrix.Core.Catalog.Distros.Common;

namespace Bootrix.Core.Catalog.Distros;

/// <param name="Variant">The edition as Fedora files it: "Workstation", "Server", "KDE", "Spins", "Everything".</param>
/// <param name="SubVariant">Which spin or image inside the edition ("Xfce", "Workstation", "Server").</param>
/// <param name="Kind">"live", "dvd" or "netinst".</param>
/// <param name="Compose">Build number inside the release ("1.7"); part of the checksum file's name.</param>
/// <param name="Architecture">Bootrix's name (x64, arm64); <paramref name="VendorArchitecture"/> is Fedora's (x86_64, aarch64).</param>
internal sealed record FedoraImage(
    int Release,
    string Variant,
    string SubVariant,
    string Kind,
    string Architecture,
    string VendorArchitecture,
    Uri Link,
    string FileName,
    string Compose,
    long? Size);

/// <summary>Fedora's <c>releases.json</c>, the list its download pages are generated from.</summary>
internal static partial class FedoraReleases
{
    /// <summary>
    /// The installable ISOs of final releases for PCs. Betas ("45 Beta"), other architectures and everything that is
    /// not an ISO (cloud images, containers) are left out.
    /// </summary>
    public static IReadOnlyList<FedoraImage> Parse(string json)
    {
        using var document = DistroJson.Parse(json, "Fedora releases.json");
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var images = new List<FedoraImage>();
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            var image = ReadEntry(entry);
            if (image is not null)
            {
                images.Add(image);
            }
        }

        return images;
    }

    private static FedoraImage? ReadEntry(JsonElement entry)
    {
        if (!int.TryParse(entry.String("version"), NumberStyles.None, CultureInfo.InvariantCulture, out var release)
            || entry.String("variant") is not { Length: > 0 } variant
            || entry.String("subvariant") is not { Length: > 0 } subVariant
            || entry.String("arch") is not { } vendorArchitecture
            || Architectures.FromVendorName(vendorArchitecture) is not { } architecture
            || !Uri.TryCreate(entry.String("link"), UriKind.Absolute, out var link)
            || !link.AbsolutePath.EndsWith(".iso", StringComparison.Ordinal))
        {
            return null;
        }

        var fileName = Uri.UnescapeDataString(link.Segments[^1]);
        var naming = ReleaseAndCompose().Match(fileName);
        if (!naming.Success)
        {
            return null;
        }

        return new FedoraImage(
            release,
            variant,
            subVariant,
            fileName.Contains("netinst", StringComparison.Ordinal) ? "netinst" : fileName.Contains("-dvd-", StringComparison.Ordinal) ? "dvd" : "live",
            architecture,
            vendorArchitecture,
            link,
            fileName,
            naming.Groups["compose"].Value,
            entry.Number("size"));
    }

    /// <summary>"…-44-1.7.x86_64.iso" (live images) and "…-x86_64-44-1.7.iso" (installers) both end in release and compose.</summary>
    [GeneratedRegex(@"-(?<release>\d+)-(?<compose>\d+\.\d+)(?:\.(?:x86_64|aarch64))?\.iso$")]
    private static partial Regex ReleaseAndCompose();
}
