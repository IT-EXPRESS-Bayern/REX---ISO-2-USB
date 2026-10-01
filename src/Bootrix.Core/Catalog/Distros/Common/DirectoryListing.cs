// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Bootrix.Core.Catalog.Distros.Common;

/// <param name="Name">Entry name as linked, without a trailing slash.</param>
/// <param name="Size">Exact size in bytes, only when the page prints one (not "4.5 GiB").</param>
internal sealed record ListingEntry(string Name, bool IsDirectory, long? Size);

/// <summary>
/// Reads the file names out of an HTML directory index (Apache, nginx, lighttpd and the like). Only relative links
/// count, which drops sort links, the parent directory and everything that points elsewhere.
/// </summary>
internal static partial class DirectoryListing
{
    public static IReadOnlyList<ListingEntry> Parse(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        var entries = new List<ListingEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match row in Row().Matches(html))
        {
            var href = WebUtility.HtmlDecode(row.Groups["href"].Value);
            if (href.StartsWith("./", StringComparison.Ordinal))
            {
                href = href[2..];
            }

            if (!IsRelativeName(href))
            {
                continue;
            }

            var isDirectory = href.EndsWith('/');
            var name = Uri.UnescapeDataString(href.TrimEnd('/'));
            if (seen.Add(name + (isDirectory ? "/" : string.Empty)))
            {
                entries.Add(new ListingEntry(name, isDirectory, ExactSize(row.Groups["tail"].Value)));
            }
        }

        return entries;
    }

    /// <summary>No sort links, no parent directory, no absolute links and no "http:" or "mailto:" targets.</summary>
    private static bool IsRelativeName(string href) =>
        href.Length > 0
        && href[0] is not ('?' or '#' or '/')
        && !href.StartsWith("..", StringComparison.Ordinal)
        && !href.Contains(':', StringComparison.Ordinal);

    private static long? ExactSize(string tail)
    {
        var size = SizeCell().Match(tail);
        return size.Success && long.TryParse(size.Groups["bytes"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes)
            ? bytes
            : null;
    }

    /// <summary>An anchor plus the text up to the next anchor, which is where the size column lives.</summary>
    [GeneratedRegex("""<a\s[^>]*?href="(?<href>[^"]*)"[^>]*>.*?</a>(?<tail>.*?)(?=<a\s|$)""", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Row();

    [GeneratedRegex("""class="size"[^>]*>\s*(?<bytes>\d+)\s*<""", RegexOptions.IgnoreCase)]
    private static partial Regex SizeCell();
}
