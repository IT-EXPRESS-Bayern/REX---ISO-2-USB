// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Nodes;
using Bootrix.Core.Errors;
using Bootrix.Core.Json;
using Bootrix.Core.Model;

namespace Bootrix.Core.Profiles;

public static class JobSpecJson
{
    public static string Serialize(JobSpec spec) =>
        JsonSerializer.Serialize(spec, CoreJson.Options);

    public static JsonObject ToNode(JobSpec spec) =>
        JsonSerializer.SerializeToNode(spec, CoreJson.Options)!.AsObject();

    public static JobSpec FromNode(JsonNode node)
    {
        var version = node["schemaVersion"]?.GetValue<int>() ?? 1;
        if (version > JobSpec.CurrentSchemaVersion)
        {
            throw new BootrixException(ErrorCode.UnsupportedSchemaVersion, version.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                Arguments = [version],
            };
        }

        node = SpecMigrations.Upgrade(node, version);

        try
        {
            return node.Deserialize<JobSpec>(CoreJson.Options)
                ?? throw new BootrixException(ErrorCode.InvalidSpec, "empty document");
        }
        catch (JsonException ex)
        {
            throw new BootrixException(ErrorCode.InvalidSpec, ex.Message, ex) { Arguments = [ex.Message] };
        }
    }

    public static JobSpec Parse(string json)
    {
        try
        {
            return FromNode(JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) ?? throw new BootrixException(ErrorCode.InvalidSpec, "empty document"));
        }
        catch (JsonException ex)
        {
            throw new BootrixException(ErrorCode.InvalidSpec, ex.Message, ex) { Arguments = [ex.Message] };
        }
    }
}

/// <summary>Upgrades older spec documents step by step. There is only one schema so far.</summary>
internal static class SpecMigrations
{
    public static JsonNode Upgrade(JsonNode node, int fromVersion)
    {
        // Each future change adds a step here, e.g. "if (fromVersion < 2) { ...rename fields...; }"
        _ = fromVersion;
        return node;
    }
}
