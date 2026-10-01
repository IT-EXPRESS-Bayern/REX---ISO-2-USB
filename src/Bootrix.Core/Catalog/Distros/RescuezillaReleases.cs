// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;
using Bootrix.Core.Catalog.Distros.Common;
using Bootrix.Core.Net;

namespace Bootrix.Core.Catalog.Distros;

/// <param name="Tag">Release tag on GitHub, "2.6.2".</param>
/// <param name="RecommendedFile">The ISO rescuezilla.com recommends for 64-bit PCs.</param>
internal sealed record RescuezillaRelease(string Tag, string RecommendedFile);

/// <param name="Base">The Ubuntu release the image is built on, by codename ("resolute", "noble").</param>
internal sealed record RescuezillaImage(string FileName, string Version, string Base, string Architecture);

internal static partial class RescuezillaReleases
{
    /// <summary>
    /// The release the download page of rescuezilla.com points to. The page names the current stable ISO with its
    /// GitHub address, which is the only place the project says which release is the current one.
    /// </summary>
    public static RescuezillaRelease? ParseDownloadPage(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        var link = ReleaseLink().Match(html);
        return link.Success ? new RescuezillaRelease(link.Groups["tag"].Value, link.Groups["file"].Value) : null;
    }

    /// <summary>The ISOs in a release's SHA256SUM asset; it also lists the Debian package of the same release.</summary>
    public static IReadOnlyList<RescuezillaImage> Images(ChecksumFile sums)
    {
        ArgumentNullException.ThrowIfNull(sums);

        return [.. sums.Entries
            .Where(e => e.FileName is not null)
            .Select(e => (Name: e.FileName!, Match: ImageName().Match(e.FileName!)))
            .Where(x => x.Match.Success)
            .Select(x => new RescuezillaImage(
                x.Name,
                x.Match.Groups["version"].Value,
                x.Match.Groups["base"].Value,
                x.Match.Groups["bits"].Value == "64" ? Architectures.X64 : Architectures.I386))
            .DistinctBy(i => i.FileName)];
    }

    public static Uri AssetUrl(string tag, string asset) =>
        new($"https://github.com/rescuezilla/rescuezilla/releases/download/{tag}/{asset}");

    [GeneratedRegex(@"https://github\.com/rescuezilla/rescuezilla/releases/download/(?<tag>[^/""']+)/(?<file>rescuezilla-[^""'<>\s]+?\.iso)")]
    private static partial Regex ReleaseLink();

    [GeneratedRegex(@"^rescuezilla-(?<version>\d+(?:\.\d+)*)-(?<bits>32|64)bit\.(?<base>[a-z]+)\.iso$")]
    private static partial Regex ImageName();
}
