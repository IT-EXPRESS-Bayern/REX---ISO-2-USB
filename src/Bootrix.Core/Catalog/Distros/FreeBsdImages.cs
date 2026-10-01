// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;
using Bootrix.Core.Catalog.Distros.Common;

namespace Bootrix.Core.Catalog.Distros;

/// <param name="Kind">"memstick", "mini-memstick", "disc1", "dvd1" or "bootonly".</param>
/// <param name="VendorArchitecture">"amd64" or "arm64-aarch64", as in the file name.</param>
internal sealed record FreeBsdImage(string FileName, string Kind, string Architecture, string VendorArchitecture, long? Size);

internal static partial class FreeBsdImages
{
    /// <summary>
    /// The newest release of each of the <paramref name="count"/> newest major versions, from the release directory
    /// listing. FreeBSD keeps the previous minor release of a branch around for a while; only the newest counts.
    /// </summary>
    public static IReadOnlyList<NumericVersion> Releases(IEnumerable<ListingEntry> listing, int count) =>
        [.. listing
            .Where(e => e.IsDirectory && NumericVersion.TryParse(e.Name, out _))
            .Select(e => NumericVersion.Parse(e.Name))
            .GroupBy(v => v.Major)
            .OrderByDescending(g => g.Key)
            .Take(count)
            .Select(g => g.Max()!)];

    /// <summary>
    /// The uncompressed images for PCs and ARM64 servers in one release directory. Compressed copies (".xz"), board
    /// images for single-board computers and other architectures are not offered.
    /// </summary>
    public static IReadOnlyList<FreeBsdImage> Parse(IEnumerable<ListingEntry> listing, string version)
    {
        var expectedPrefix = $"FreeBSD-{version}-RELEASE-";

        return [.. listing
            .Where(e => !e.IsDirectory && e.Name.StartsWith(expectedPrefix, StringComparison.Ordinal))
            .Select(e => (Entry: e, Match: ImageName().Match(e.Name[expectedPrefix.Length..])))
            .Where(x => x.Match.Success)
            .Select(x => new FreeBsdImage(
                x.Entry.Name,
                x.Match.Groups["kind"].Value,
                x.Match.Groups["arch"].Value == "amd64" ? Architectures.X64 : Architectures.Arm64,
                x.Match.Groups["arch"].Value,
                x.Entry.Size))];
    }

    public static string ChecksumFileName(string version, string vendorArchitecture) =>
        $"CHECKSUM.SHA256-FreeBSD-{version}-RELEASE-{vendorArchitecture}";

    [GeneratedRegex(@"^(?<arch>amd64|arm64-aarch64)-(?<kind>memstick|mini-memstick|disc1|dvd1|bootonly)\.(?:iso|img)$")]
    private static partial Regex ImageName();
}
