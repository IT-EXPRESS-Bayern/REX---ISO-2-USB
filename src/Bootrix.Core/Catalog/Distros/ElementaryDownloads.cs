// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;
using Bootrix.Core.Catalog.Distros.Common;

namespace Bootrix.Core.Catalog.Distros;

/// <param name="Url">Download address including the time-limited code in its first path segment.</param>
internal sealed record ElementaryDownload(string Version, string Architecture, string VendorArchitecture, string FileName, DateOnly? Build, Uri Url);

/// <summary>The download links on elementary.io. The site hands out each file under an address with a code that expires after three days.</summary>
internal static partial class ElementaryDownloads
{
    public static IReadOnlyList<ElementaryDownload> Parse(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        var downloads = new List<ElementaryDownload>();
        foreach (Match match in DownloadLink().Matches(html))
        {
            var vendorArchitecture = match.Groups["arch"].Value;
            DateOnly? build = DateOnly.TryParseExact(match.Groups["build"].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

            downloads.Add(new ElementaryDownload(
                match.Groups["version"].Value,
                Architectures.FromVendorName(vendorArchitecture)!,
                vendorArchitecture,
                match.Groups["file"].Value,
                build,
                new Uri(match.Groups["url"].Value)));
        }

        return [.. downloads.DistinctBy(d => d.FileName)];
    }

    [GeneratedRegex(@"""(?<url>https://dl\.elementaryos\.org/[^/""]+/(?<file>elementaryos-(?<version>\d+(?:\.\d+)*)-stable-(?<arch>amd64|arm64)\.(?<build>\d{8})\.iso))""")]
    private static partial Regex DownloadLink();
}
