// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;
using Bootrix.Core.Catalog.Distros.Common;
using Bootrix.Core.Net;

namespace Bootrix.Core.Catalog.Distros;

/// <param name="Series">Directory name on the image servers, "26.04".</param>
/// <param name="Version">Newest point release, "26.04.1".</param>
internal sealed record UbuntuRelease(string Dist, string Series, NumericVersion Version, bool IsLts, bool Supported, DateOnly? Date);

/// <summary>An image file of one point release; <see cref="Kind"/> is "desktop" or "server".</summary>
internal sealed record UbuntuImage(string FileName, NumericVersion Version, string Kind);

internal static partial class UbuntuReleases
{
    private const string DateFormat = "ddd, d MMMM yyyy HH:mm:ss 'UTC'";

    /// <summary>Reads Canonical's meta-release list, the file update-manager uses to learn which releases exist.</summary>
    public static IReadOnlyList<UbuntuRelease> ParseMetaRelease(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var releases = new List<UbuntuRelease>();
        foreach (var block in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in block.Split('\n'))
            {
                var colon = line.IndexOf(':', StringComparison.Ordinal);
                if (colon > 0)
                {
                    fields[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }
            }

            if (!fields.TryGetValue("Dist", out var dist) || !fields.TryGetValue("Version", out var versionText))
            {
                continue;
            }

            var isLts = versionText.Contains("LTS", StringComparison.OrdinalIgnoreCase);
            if (!NumericVersion.TryParse(versionText.Split(' ')[0], out var version))
            {
                continue;
            }

            DateOnly? date = fields.TryGetValue("Date", out var dateText)
                && DateTimeOffset.TryParseExact(dateText, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                    ? DateOnly.FromDateTime(parsed.UtcDateTime)
                    : null;

            releases.Add(new UbuntuRelease(
                dist,
                $"{version.Major}.{version.Minor:D2}",
                version,
                isLts,
                fields.TryGetValue("Supported", out var supported) && supported == "1",
                date));
        }

        return releases;
    }

    /// <summary>
    /// The newest point release of each image kind. A checksum file keeps the lines of older point releases, so
    /// "24.04.3", "24.04.4" and "24.04.5" can all be in it; only the highest one is offered.
    /// </summary>
    public static IReadOnlyList<UbuntuImage> LatestImages(ChecksumFile sums, string prefix)
    {
        ArgumentNullException.ThrowIfNull(sums);

        return [.. sums.Entries
            .Where(e => e.FileName is not null)
            .Select(e => ParseImage(e.FileName!, prefix))
            .OfType<UbuntuImage>()
            .GroupBy(i => i.Kind)
            .Select(g => g.MaxBy(i => i.Version)!)
            .OrderBy(i => i.Kind, StringComparer.Ordinal)];
    }

    private static UbuntuImage? ParseImage(string fileName, string prefix)
    {
        var match = ImageName().Match(fileName);
        if (!match.Success || match.Groups["prefix"].Value != prefix || !NumericVersion.TryParse(match.Groups["version"].Value, out var version))
        {
            return null;
        }

        return new UbuntuImage(fileName, version, match.Groups["kind"].Value == "desktop" ? "desktop" : "server");
    }

    [GeneratedRegex(@"^(?<prefix>[a-z]+(?:-[a-z]+)*?)-(?<version>\d+(?:\.\d+){1,3})-(?<kind>desktop|live-server)-amd64\.iso$")]
    private static partial Regex ImageName();
}
