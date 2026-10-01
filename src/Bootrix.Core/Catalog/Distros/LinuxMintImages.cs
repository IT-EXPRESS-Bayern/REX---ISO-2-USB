// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;
using Bootrix.Core.Catalog.Distros.Common;
using Bootrix.Core.Net;

namespace Bootrix.Core.Catalog.Distros;

/// <param name="Edition">"cinnamon", "mate" or "xfce".</param>
/// <param name="IsEdge">The edge image carries a newer kernel for recent hardware.</param>
internal sealed record LinuxMintImage(string FileName, NumericVersion Version, string Edition, bool IsEdge);

internal static partial class LinuxMintImages
{
    /// <summary>Release directories ("22.3") found in the mirror's listing, newest first.</summary>
    public static IReadOnlyList<NumericVersion> ReleaseDirectories(IEnumerable<ListingEntry> listing) =>
        [.. listing
            .Where(e => e.IsDirectory && NumericVersion.TryParse(e.Name, out _))
            .Select(e => NumericVersion.Parse(e.Name))
            .OrderByDescending(v => v)];

    /// <summary>The newest point release of each of the <paramref name="count"/> newest major series; older ones are out of support.</summary>
    public static IReadOnlyList<NumericVersion> NewestOfEachSeries(IEnumerable<NumericVersion> versions, int count) =>
        [.. versions
            .GroupBy(v => v.Major)
            .OrderByDescending(g => g.Key)
            .Take(count)
            .Select(g => g.Max()!)];

    public static IReadOnlyList<LinuxMintImage> Parse(ChecksumFile sums)
    {
        ArgumentNullException.ThrowIfNull(sums);

        return [.. sums.Entries
            .Where(e => e.FileName is not null)
            .Select(e => (Name: e.FileName!, Match: ImageName().Match(e.FileName!)))
            .Where(x => x.Match.Success && NumericVersion.TryParse(x.Match.Groups["version"].Value, out _))
            .Select(x => new LinuxMintImage(
                x.Name,
                NumericVersion.Parse(x.Match.Groups["version"].Value),
                x.Match.Groups["edition"].Value,
                x.Match.Groups["edge"].Success))];
    }

    [GeneratedRegex(@"^linuxmint-(?<version>\d+(?:\.\d+)?)-(?<edition>cinnamon|mate|xfce)-64bit(?<edge>-edge)?\.iso$")]
    private static partial Regex ImageName();
}
