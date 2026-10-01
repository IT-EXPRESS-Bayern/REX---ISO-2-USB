// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Catalog.Distros.Common;

/// <summary>Builds and reads <see cref="CatalogVariant.Properties"/>; the trust level is always part of them.</summary>
internal static class VariantProperties
{
    public static IReadOnlyDictionary<string, string> Signed(params (string Key, string Value)[] values) =>
        Create(DistroProperties.PinnedKey, values);

    public static IReadOnlyDictionary<string, string> Unsigned(params (string Key, string Value)[] values) =>
        Create(DistroProperties.TlsOnly, values);

    /// <summary>A value the provider stored when it listed the variant; its absence means the variant came from somewhere else.</summary>
    public static string Property(this CatalogVariant variant, string key) =>
        variant.Properties.TryGetValue(key, out var value)
            ? value
            : throw new ArgumentException($"Variant '{variant.Id}' has no '{key}'; it was not produced by this provider.", nameof(variant));

    private static Dictionary<string, string> Create(string trust, (string Key, string Value)[] values)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal) { [DistroProperties.HashTrust] = trust };
        foreach (var (key, value) in values)
        {
            properties[key] = value;
        }

        return properties;
    }
}
