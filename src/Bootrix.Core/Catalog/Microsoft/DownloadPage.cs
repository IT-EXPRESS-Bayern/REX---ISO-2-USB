// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Bootrix.Core.Net;

namespace Bootrix.Core.Catalog.Microsoft;

internal sealed record PageEdition(int Id, string Label);

/// <summary>A SHA-256 as printed in the table of the page's "verify your download" section, keyed by the language label.</summary>
internal sealed record PublishedHash(string Language, int Bits, FileHash Sha256);

/// <summary>
/// What the scripts of a software-download page are configured with: where the API lives, which editions the
/// drop-down offers, and the published ISO digests. A pure function over the page's HTML.
/// </summary>
internal sealed partial record DownloadPage(
    Uri ApiBase,
    Uri FingerprintScript,
    IReadOnlyList<PageEdition> Editions,
    IReadOnlyList<PublishedHash> Hashes)
{
    // Both are written into the page's script as fallbacks and are what a browser uses when the page names nothing else.
    private static readonly Uri DefaultApiBase = new("https://www.microsoft.com/software-download-connector/api/");
    private static readonly Uri DefaultFingerprintScript = new("https://ov-df.microsoft.com/mdt.js?instanceId=560dc9f3-1aa5-4a2f-b63c-9e18f8d0e175&pageId=si&session_id=");

    /// <exception cref="InvalidDataException">The page has no edition list, which means its layout changed.</exception>
    public static DownloadPage Parse(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        var editions = new List<PageEdition>();
        foreach (Match select in EditionSelect().Matches(html))
        {
            foreach (Match option in Option().Matches(select.Groups["body"].Value))
            {
                var id = int.Parse(option.Groups["id"].Value, CultureInfo.InvariantCulture);
                if (editions.All(e => e.Id != id))
                {
                    editions.Add(new PageEdition(id, WebUtility.HtmlDecode(option.Groups["label"].Value).Trim()));
                }
            }
        }

        if (editions.Count == 0)
        {
            throw new InvalidDataException("the page has no <select id=\"product-edition…\"> with numeric edition ids");
        }

        return new DownloadPage(
            HttpsAddress(InputValue(html, "endpoint-svc")) ?? DefaultApiBase,
            HttpsAddress(InputValue(html, "ov-df-ref")) ?? DefaultFingerprintScript,
            editions,
            [.. HashRow().Matches(html).Select(m => new PublishedHash(
                Normalize(WebUtility.HtmlDecode(m.Groups["name"].Value)),
                int.Parse(m.Groups["bits"].Value, CultureInfo.InvariantCulture),
                new FileHash(HashKind.Sha256, m.Groups["hash"].Value)))]);
    }

    /// <summary>
    /// The digest for a language and word size, if the table has exactly one. Microsoft writes a language
    /// differently here than in the API ("English International" against "English (United Kingdom)"), so every
    /// name the API gave is tried; a missing or ambiguous row yields null rather than a guess.
    /// </summary>
    public FileHash? FindHash(IEnumerable<string> languageNames, int bits)
    {
        var wanted = languageNames.Select(Normalize).ToHashSet(StringComparer.Ordinal);
        var matches = Hashes.Where(h => h.Bits == bits && wanted.Contains(h.Language)).Select(h => h.Sha256).Distinct().ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static string Normalize(string name) =>
        new([.. name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);

    private static string? InputValue(string html, string id)
    {
        foreach (Match input in InputTag().Matches(html))
        {
            var tag = input.Value;
            var idMatch = IdAttribute().Match(tag);
            if (!idMatch.Success || idMatch.Groups["value"].Value != id)
            {
                continue;
            }

            var value = ValueAttribute().Match(tag);
            return value.Success ? WebUtility.HtmlDecode(value.Groups["value"].Value) : null;
        }

        return null;
    }

    private static Uri? HttpsAddress(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        return uri;
    }

    [GeneratedRegex(@"<select\b(?=[^>]*(?<![\w-])id\s*=\s*[""']product-edition[\w-]*[""'])[^>]*>(?<body>.*?)</select>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex EditionSelect();

    // Closing tags are optional in HTML, so the label simply runs to the next tag.
    [GeneratedRegex(@"<option\b[^>]*(?<![\w-])value\s*=\s*[""'](?<id>\d+)[""'][^>]*>(?<label>[^<]*)", RegexOptions.IgnoreCase)]
    private static partial Regex Option();

    [GeneratedRegex(@"<input\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex InputTag();

    [GeneratedRegex(@"(?<![\w-])id\s*=\s*[""'](?<value>[^""']*)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex IdAttribute();

    [GeneratedRegex(@"(?<![\w-])value\s*=\s*[""'](?<value>[^""']*)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex ValueAttribute();

    [GeneratedRegex(@"<tr\b[^>]*>\s*<td\b[^>]*>\s*(?<name>[^<]*?)\s+(?<bits>32|64)-bit\s*</td>\s*<td\b[^>]*>\s*(?<hash>[0-9A-Fa-f]{64})\s*</td>\s*</tr>", RegexOptions.IgnoreCase)]
    private static partial Regex HashRow();
}
