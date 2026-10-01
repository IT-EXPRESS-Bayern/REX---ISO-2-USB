// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Bootrix.Core.Catalog.Rescue;

/// <summary>A text in both supported languages; the catalog is data, so its descriptions travel with it instead of living in resource files.</summary>
public sealed record LocalizedText(string De, string En)
{
    /// <summary>German for German cultures, English for everything else, as in <c>LanguageChoice</c>.</summary>
    public string For(CultureInfo culture) =>
        string.Equals(culture.TwoLetterISOLanguageName, "de", StringComparison.OrdinalIgnoreCase) ? De : En;
}
