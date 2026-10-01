// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;

namespace Bootrix.Core.Catalog.Microsoft;

/// <summary>Language names and codes as Microsoft writes them, mapped to BCP 47 tags such as "de-DE".</summary>
internal static class LanguageTags
{
    // Names of the download pages; the ISO file names and the Media Creation Tool catalog use the same set.
    private static readonly Dictionary<string, string> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Arabic"] = "ar-SA",
        ["Brazilian Portuguese"] = "pt-BR",
        ["Bulgarian"] = "bg-BG",
        ["Chinese (Simplified)"] = "zh-CN",
        ["Chinese Simplified"] = "zh-CN",
        ["Chinese (Traditional)"] = "zh-TW",
        ["Chinese Traditional"] = "zh-TW",
        ["Croatian"] = "hr-HR",
        ["Czech"] = "cs-CZ",
        ["Danish"] = "da-DK",
        ["Dutch"] = "nl-NL",
        ["English"] = "en-US",
        ["English (United States)"] = "en-US",
        ["English (United Kingdom)"] = "en-GB",
        ["English International"] = "en-GB",
        ["Estonian"] = "et-EE",
        ["Finnish"] = "fi-FI",
        ["French"] = "fr-FR",
        ["French Canadian"] = "fr-CA",
        ["German"] = "de-DE",
        ["Greek"] = "el-GR",
        ["Hebrew"] = "he-IL",
        ["Hungarian"] = "hu-HU",
        ["Italian"] = "it-IT",
        ["Japanese"] = "ja-JP",
        ["Korean"] = "ko-KR",
        ["Latvian"] = "lv-LV",
        ["Lithuanian"] = "lt-LT",
        ["Norwegian"] = "nb-NO",
        ["Polish"] = "pl-PL",
        ["Portuguese"] = "pt-PT",
        ["Romanian"] = "ro-RO",
        ["Russian"] = "ru-RU",
        ["Serbian Latin"] = "sr-Latn-RS",
        ["Slovak"] = "sk-SK",
        ["Slovenian"] = "sl-SI",
        ["Spanish"] = "es-ES",
        ["Spanish (Mexico)"] = "es-MX",
        ["Swedish"] = "sv-SE",
        ["Thai"] = "th-TH",
        ["Turkish"] = "tr-TR",
        ["Ukrainian"] = "uk-UA",
    };

    /// <summary>Tag for a language name from the download pages; null for names this table does not know.</summary>
    public static string? FromName(string name) => ByName.GetValueOrDefault(name.Trim());

    /// <summary>Brings "sr-latn-rs" into the usual casing "sr-Latn-RS": language lower, script title case, region upper.</summary>
    public static string Normalize(string tag)
    {
        var parts = tag.Trim().Split('-', StringSplitOptions.RemoveEmptyEntries);
        var result = new StringBuilder();

        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0)
            {
                result.Append('-');
            }

            var part = parts[i];
            result.Append(i == 0 ? part.ToLowerInvariant()
                : part.Length == 4 ? char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()
                : part.ToUpperInvariant());
        }

        return result.ToString();
    }
}
