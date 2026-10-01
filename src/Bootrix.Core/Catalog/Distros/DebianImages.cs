// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;
using Bootrix.Core.Net;

namespace Bootrix.Core.Catalog.Distros;

/// <param name="Edition">"netinst", or the desktop of a live image ("gnome", "kde", "debian-junior").</param>
internal sealed record DebianImage(string FileName, string Version, string Edition, string Architecture);

/// <summary>Picks the images of the current stable release out of the checksum files in Debian's image directories.</summary>
internal static partial class DebianImages
{
    /// <summary>The generic installer; the "edu" and "mac" netinst images in the same directory are special purpose.</summary>
    public static IReadOnlyList<DebianImage> Netinst(ChecksumFile sums) =>
        Select(sums, NetinstName(), _ => "netinst");

    public static IReadOnlyList<DebianImage> Live(ChecksumFile sums) =>
        Select(sums, LiveName(), m => m.Groups["edition"].Value);

    private static List<DebianImage> Select(ChecksumFile sums, Regex pattern, Func<Match, string> edition)
    {
        ArgumentNullException.ThrowIfNull(sums);

        // The same file name also appears with .contents, .log and .packages suffixes; the anchor in the pattern drops those.
        return [.. sums.Entries
            .Where(e => e.FileName is not null)
            .Select(e => (Name: e.FileName!, Match: pattern.Match(e.FileName!)))
            .Where(x => x.Match.Success)
            .Select(x => new DebianImage(x.Name, x.Match.Groups["version"].Value, edition(x.Match), x.Match.Groups["arch"].Value))
            .DistinctBy(i => i.FileName)];
    }

    [GeneratedRegex(@"^debian-(?<version>\d+(?:\.\d+)*)-(?<arch>amd64|arm64)-netinst\.iso$")]
    private static partial Regex NetinstName();

    [GeneratedRegex(@"^debian-live-(?<version>\d+(?:\.\d+)*)-(?<arch>amd64)-(?<edition>[a-z]+(?:-[a-z]+)*)\.iso$")]
    private static partial Regex LiveName();
}
