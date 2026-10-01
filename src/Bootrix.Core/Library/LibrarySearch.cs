// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Library;

internal static class LibrarySearch
{
    private const int MinHashPrefix = 4;

    /// <summary>
    /// Every word of the query has to match: as part of the name, version, product, variant, architecture or language
    /// (any case), or, for four or more hex digits, as the start of the SHA-256. The stored file name is not searched,
    /// since apart from the extension it is the hash and a short word would match it by chance. The most recently used first.
    /// </summary>
    public static IReadOnlyList<LibraryEntry> Filter(IEnumerable<LibraryEntry> entries, string query)
    {
        var words = (query ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return
        [
            .. entries
                .Where(e => words.All(w => Matches(e, w)))
                .OrderByDescending(e => e.LastUsedUtc)
                .ThenBy(e => e.Info.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(e => e.Sha256, StringComparer.Ordinal),
        ];
    }

    private static bool Matches(LibraryEntry entry, string word)
    {
        if (word.Length >= MinHashPrefix && word.All(char.IsAsciiHexDigit) && entry.Sha256.StartsWith(word, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var info = entry.Info;
        return new[] { info.Name, info.Version, info.CatalogId, info.VariantId, info.Architecture, info.Language }
            .Any(field => field?.Contains(word, StringComparison.OrdinalIgnoreCase) == true);
    }
}
