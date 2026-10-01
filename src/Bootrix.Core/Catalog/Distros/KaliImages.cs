// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;
using Bootrix.Core.Catalog.Distros.Common;

namespace Bootrix.Core.Catalog.Distros;

/// <param name="Kind">"installer", "installer-netinst", "installer-purple", "live" and so on, as in the file name.</param>
internal sealed record KaliImage(string Version, string Kind, IReadOnlyList<string> Architectures);

internal static partial class KaliImages
{
    /// <summary>
    /// The ISOs that are really on the server. The checksum file also lists images that are only distributed by
    /// torrent, so the directory page, not the checksum file, says what can be downloaded.
    /// </summary>
    public static IReadOnlyList<KaliImage> Parse(IEnumerable<ListingEntry> listing) =>
        [.. listing
            .Where(e => !e.IsDirectory)
            .Select(e => ImageName().Match(e.Name))
            .Where(m => m.Success)
            .GroupBy(m => (Version: m.Groups["version"].Value, Kind: m.Groups["kind"].Value))
            .OrderByDescending(g => g.Key.Version, StringComparer.Ordinal)
            .ThenBy(g => KindRank(g.Key.Kind))
            .Select(g => new KaliImage(
                g.Key.Version,
                g.Key.Kind,
                [.. g.Select(m => Architectures.FromVendorName(m.Groups["arch"].Value)).OfType<string>().Distinct().OrderByDescending(a => a == Architectures.X64)]))];

    public static string FileName(string version, string kind, string vendorArchitecture) =>
        $"kali-linux-{version}-{kind}-{vendorArchitecture}.iso";

    private static int KindRank(string kind) => kind switch
    {
        "installer" => 0,
        "installer-netinst" => 1,
        "installer-purple" => 2,
        "installer-everything" => 3,
        "live" => 4,
        _ => 5,
    };

    [GeneratedRegex(@"^kali-linux-(?<version>\d{4}\.\d+[a-z]?)-(?<kind>installer(?:-netinst|-purple|-everything)?|live(?:-everything)?)-(?<arch>amd64|arm64)\.iso$")]
    private static partial Regex ImageName();
}
