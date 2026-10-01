// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.Json;

namespace Bootrix.Core.Catalog.Microsoft;

/// <summary>An entry of the <c>Errors</c> list; the API answers HTTP 200 and reports refusals in the body.</summary>
internal sealed record ApiError(string Key, string Value)
{
    /// <summary>Microsoft's name for "this request looks automated or anonymised"; the page shows it as message code 715-123130.</summary>
    public const string SentinelRejectKey = "ErrorSettings.SentinelReject";

    public bool IsSentinelReject => Key == SentinelRejectKey;

    public override string ToString() => string.IsNullOrEmpty(Value) ? Key : $"{Key}: {Value}";
}

/// <param name="Id">Identifier of the language edition, valid for the current build only.</param>
/// <param name="Language">English name such as "German" or "English (United Kingdom)".</param>
internal sealed record SkuEntry(string Id, string Language, string LocalizedLanguage, string ProductName);

internal sealed record SkuResponse(IReadOnlyList<SkuEntry> Skus, IReadOnlyList<ApiError> Errors);

/// <param name="Architecture">"x86", "x64" or "arm64"; null if neither the type field nor the file name says.</param>
internal sealed record LinkEntry(Uri Url, string? Architecture, string Language, string ProductName);

internal sealed record LinkResponse(IReadOnlyList<LinkEntry> Links, DateTimeOffset? Expires, IReadOnlyList<ApiError> Errors);

/// <summary>Reads the JSON of <c>getskuinformationbyproductedition</c> and <c>GetProductDownloadLinksBySku</c>.</summary>
internal static class MicrosoftApiResponses
{
    /// <exception cref="InvalidDataException">The text is not the JSON object the API sends.</exception>
    public static SkuResponse ParseSkus(string json)
    {
        using var document = Open(json);
        var root = document.RootElement;

        var skus = new List<SkuEntry>();
        foreach (var sku in Items(root, "Skus"))
        {
            var id = Text(sku, "Id") ?? throw new InvalidDataException("a SKU entry has no Id");
            var language = Text(sku, "Language") ?? throw new InvalidDataException($"SKU {id} has no Language");
            skus.Add(new SkuEntry(id, language, Text(sku, "LocalizedLanguage") ?? language, Text(sku, "ProductDisplayName") ?? string.Empty));
        }

        return new SkuResponse(skus, Errors(root));
    }

    /// <exception cref="InvalidDataException">The text is not the JSON object the API sends, or a link is not an https address.</exception>
    public static LinkResponse ParseLinks(string json)
    {
        using var document = Open(json);
        var root = document.RootElement;

        var links = new List<LinkEntry>();
        foreach (var option in Items(root, "ProductDownloadOptions"))
        {
            var text = Text(option, "Uri") ?? throw new InvalidDataException("a download option has no Uri");
            if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
            {
                throw new InvalidDataException("a download option has no https address");
            }

            var type = option.TryGetProperty("DownloadType", out var number) && number.ValueKind == JsonValueKind.Number && number.TryGetInt32(out var t) ? t : (int?)null;
            links.Add(new LinkEntry(
                url,
                ArchitectureOf(type, url),
                Text(option, "Language") ?? string.Empty,
                Text(option, "ProductDisplayName") ?? Text(option, "Name") ?? string.Empty));
        }

        DateTimeOffset? expires = DateTimeOffset.TryParse(Text(root, "DownloadExpirationDatetime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
        return new LinkResponse(links, expires, Errors(root));
    }

    /// <summary>
    /// The download type is Microsoft's own numbering (0 = 32-bit, 1 = 64-bit, 2 = Arm64); the file name is the
    /// fallback for a type this table does not know.
    /// </summary>
    private static string? ArchitectureOf(int? type, Uri url)
    {
        switch (type)
        {
            case 0:
                return "x86";
            case 1:
                return "x64";
            case 2:
                return "arm64";
        }

        var file = Uri.UnescapeDataString(url.Segments[^1]).ToLowerInvariant();
        return file.Contains("arm64", StringComparison.Ordinal) ? "arm64"
            : file.Contains("x64", StringComparison.Ordinal) ? "x64"
            : file.Contains("x32", StringComparison.Ordinal) || file.Contains("x86", StringComparison.Ordinal) ? "x86"
            : null;
    }

    private static JsonDocument Open(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"the answer is not JSON: {ex.Message}", ex);
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new InvalidDataException("the answer is not a JSON object");
        }

        return document;
    }

    private static JsonElement.ArrayEnumerator Items(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array ? list.EnumerateArray() : default;

    private static string? Text(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    /// <summary>Errors sit at the top level when the whole request is refused and under <c>ValidationContainer</c> otherwise.</summary>
    private static List<ApiError> Errors(JsonElement root)
    {
        var errors = new List<ApiError>();

        void Collect(JsonElement container)
        {
            foreach (var error in Items(container, "Errors"))
            {
                // An entry of a shape we do not know is still a refusal; keep what it says.
                errors.Add(new ApiError(Text(error, "Key") ?? "unknown", Text(error, "Value") ?? error.GetRawText()));
            }
        }

        Collect(root);
        if (root.TryGetProperty("ValidationContainer", out var container) && container.ValueKind == JsonValueKind.Object)
        {
            Collect(container);
        }

        return errors;
    }
}
