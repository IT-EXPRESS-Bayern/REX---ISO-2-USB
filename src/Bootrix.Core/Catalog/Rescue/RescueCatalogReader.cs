// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.Json;
using static Bootrix.Core.Catalog.Rescue.CatalogRules;

namespace Bootrix.Core.Catalog.Rescue;

/// <summary>
/// Turns catalog JSON into a <see cref="RescueCatalogDocument"/> and rejects what does not meet the schema. The same
/// checks run for the embedded file and for documents from the signed channel: a signature proves who published a
/// catalog, not that it is well formed or that it carries the notice every password tool needs.
/// </summary>
internal static class RescueCatalogReader
{
    private const int MaxToolNameLength = 64;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static RescueCatalogDocument Read(Stream json)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            return Map(JsonSerializer.Deserialize<CatalogFile>(json, Options) ?? throw Invalid("the document is empty"));
        }
        catch (JsonException ex)
        {
            throw Invalid(ex.Message, ex);
        }
    }

    public static RescueCatalogDocument Read(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("the document is not a JSON object");
        }

        try
        {
            return Map(json.Deserialize<CatalogFile>(Options) ?? throw Invalid("the document is empty"));
        }
        catch (JsonException ex)
        {
            throw Invalid(ex.Message, ex);
        }
    }

    private static RescueCatalogDocument Map(CatalogFile file)
    {
        if (file.SchemaVersion != RescueCatalogDocument.CurrentSchema)
        {
            throw Invalid($"unsupported schema version {file.SchemaVersion?.ToString(CultureInfo.InvariantCulture) ?? "(none)"}");
        }

        if (file.Version is not > 0)
        {
            throw Invalid("version must be a positive number");
        }

        if (file.Issued is not { } issued || file.Expires is not { } expires || expires <= issued)
        {
            throw Invalid("issued and expires must be dates, with expires after issued");
        }

        if (file.Entries is not { Count: > 0 } entries)
        {
            throw Invalid("the catalog has no entries");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var mapped = new List<RescueEntry>(entries.Count);
        foreach (var entry in entries)
        {
            var result = MapEntry(entry ?? throw Invalid("an entry is empty"));
            if (!ids.Add(result.Id))
            {
                throw Invalid($"entry id '{result.Id}' is used twice");
            }

            mapped.Add(result);
        }

        return new RescueCatalogDocument { Version = file.Version.Value, Issued = issued, Expires = expires, Entries = mapped };
    }

    private static RescueEntry MapEntry(EntryFile entry)
    {
        var id = entry.Id ?? string.Empty;
        if (!IsSlug(id))
        {
            throw Invalid($"entry id '{entry.Id}' must be lower case letters, digits and dashes");
        }

        var where = $"entry '{id}'";
        var name = Required(entry.Name, where + ": name");

        if (entry.Description is not { De: { } de, En: { } en } || string.IsNullOrWhiteSpace(de) || string.IsNullOrWhiteSpace(en))
        {
            throw Invalid($"{where}: description needs a German and an English text");
        }

        if (!RescueNames.TryParse<RescueCategory>(entry.Category, out var category))
        {
            throw Invalid($"{where}: unknown category '{entry.Category}'");
        }

        var license = Required(entry.License, where + ": license");
        if (!IsLicense(license))
        {
            throw Invalid($"{where}: '{license}' is neither an SPDX expression nor a proprietary tag");
        }

        var notices = Keys(entry.Notices, "Notice.", where);
        if (category == RescueCategory.OfflinePasswordReset && !notices.Contains(RescueNotices.AuthorizedUseOnly))
        {
            throw Invalid($"{where}: a password reset tool must carry {RescueNotices.AuthorizedUseOnly}");
        }

        if (entry.Variants is not { Count: > 0 } variants)
        {
            throw Invalid($"{where}: at least one variant is required");
        }

        var mapped = variants.Select(v => RescueVariantReader.Map(v ?? throw Invalid($"{where}: a variant is empty"), where)).ToList();
        RejectRepeats(mapped, where);

        return new RescueEntry
        {
            Id = id,
            Name = name,
            Description = new LocalizedText(de.Trim(), en.Trim()),
            Category = category,
            License = license,
            Homepage = WebAddress(entry.Homepage, where + ": homepage"),
            IncludedTools = Tools(entry.IncludedTools, where),
            Notices = notices,
            Hints = Keys(entry.Hints, "Hint.", where),
            Variants = mapped,
        };
    }

    private static void RejectRepeats(List<RescueVariant> variants, string where)
    {
        var duplicate = variants.GroupBy(v => v.Id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw Invalid($"{where}: variant id '{duplicate.Key}' is used twice");
        }

        if (variants.Count(v => v.Recommended) > 1)
        {
            throw Invalid($"{where}: only one variant can be recommended");
        }
    }

    private static List<string> Keys(List<string>? keys, string prefix, string where)
    {
        var result = new List<string>();
        foreach (var key in keys ?? [])
        {
            // A key is looked up in the UI texts; keeping to one prefix and plain characters stops a document from probing other resources.
            if (key is null || !key.StartsWith(prefix, StringComparison.Ordinal) || key.Length == prefix.Length || !key.All(c => char.IsAsciiLetterOrDigit(c) || c == '.'))
            {
                throw Invalid($"{where}: '{key}' is not a {prefix}* text key");
            }

            if (!result.Contains(key))
            {
                result.Add(key);
            }
        }

        return result;
    }

    private static List<string> Tools(List<string>? tools, string where)
    {
        var result = new List<string>();
        foreach (var tool in tools ?? [])
        {
            if (string.IsNullOrWhiteSpace(tool) || tool.Length > MaxToolNameLength)
            {
                throw Invalid($"{where}: a tool name is empty or longer than {MaxToolNameLength} characters");
            }

            result.Add(tool.Trim());
        }

        return result;
    }
}
