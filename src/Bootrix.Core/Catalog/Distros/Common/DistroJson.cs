// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.Json;
using Bootrix.Core.Errors;

namespace Bootrix.Core.Catalog.Distros.Common;

/// <summary>Reading the vendors' JSON without trusting its shape: missing or odd fields are skipped, broken documents are errors.</summary>
internal static class DistroJson
{
    /// <summary>A document that is not JSON means the vendor changed or broke its service, so it surfaces as an unavailable catalog.</summary>
    public static JsonDocument Parse(string text, string source)
    {
        try
        {
            return JsonDocument.Parse(text);
        }
        catch (JsonException ex)
        {
            throw new BootrixException(ErrorCode.CatalogUnavailable, $"{source}: not valid JSON ({ex.Message})", ex);
        }
    }

    public static string? String(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>A number that a vendor may have written as a JSON number or as a string ("2851612672").</summary>
    public static long? Number(this JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    public static JsonElement? Child(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : null;
}
