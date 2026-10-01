// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Bootrix.Core.Json;

/// <summary>
/// Shared serializer settings. The reflection-based serializer is used on purpose: source-generated
/// metadata fills init-only properties through an object initializer and therefore discards the
/// default values written in the type, so a document with a missing section would not fall back to defaults.
/// </summary>
public static class CoreJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            RespectNullableAnnotations = false,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
