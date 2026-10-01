// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;

namespace Bootrix.Core.Catalog.Rescue;

/// <summary>The catalog file spells enum members in kebab case ("win-pe-repair", "raw-dd"); this is the one place that knows the mapping.</summary>
internal static class RescueNames
{
    public static string Of<T>(T value)
        where T : struct, Enum => JsonNamingPolicy.KebabCaseLower.ConvertName(value.ToString());

    public static bool TryParse<T>(string? text, out T value)
        where T : struct, Enum
    {
        value = default;
        return text is not null && Lookup<T>.Values.TryGetValue(text, out value);
    }

    private static class Lookup<T>
        where T : struct, Enum
    {
        public static readonly Dictionary<string, T> Values =
            Enum.GetValues<T>().ToDictionary(v => Of(v), v => v, StringComparer.Ordinal);
    }
}
