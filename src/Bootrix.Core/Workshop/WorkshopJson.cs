// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Bootrix.Core.Json;

namespace Bootrix.Core.Workshop;

/// <summary>
/// Serializer settings for everything that leaves the workshop code as JSON. <see cref="Options"/> is what
/// <see cref="CoreJson.Options"/> is plus the removal of every <see cref="SensitiveAttribute"/> property, so output meant for
/// the screen, a log or a report cannot contain a key by accident. The unredacted <see cref="CoreJson.Options"/> is for the
/// one place that writes the encrypted customer sheet.
/// </summary>
public static class WorkshopJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(CoreJson.Options)
        {
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { RemoveSensitiveProperties } },
        };
        options.MakeReadOnly();
        return options;
    }

    private static void RemoveSensitiveProperties(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        for (var i = typeInfo.Properties.Count - 1; i >= 0; i--)
        {
            if (typeInfo.Properties[i].AttributeProvider?.IsDefined(typeof(SensitiveAttribute), inherit: true) == true)
            {
                typeInfo.Properties.RemoveAt(i);
            }
        }
    }
}
