// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Bootrix.Core.Localization;

/// <summary>Turns the language setting into a culture. German and English exist; everything else gets English.</summary>
public static class LanguageChoice
{
    public static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
    public static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    public static CultureInfo Resolve(string? setting, CultureInfo systemUiCulture)
    {
        if (!string.IsNullOrWhiteSpace(setting))
        {
            try
            {
                return Supported(CultureInfo.GetCultureInfo(setting));
            }
            catch (CultureNotFoundException)
            {
                // A hand-edited settings file with a bogus name falls through to the system language.
            }
        }

        return Supported(systemUiCulture);
    }

    private static CultureInfo Supported(CultureInfo culture) => culture.TwoLetterISOLanguageName switch
    {
        "de" => culture.IsNeutralCulture ? German : culture,
        "en" => culture.IsNeutralCulture ? English : culture,
        _ => English,
    };
}
