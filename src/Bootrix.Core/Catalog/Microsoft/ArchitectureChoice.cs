// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Catalog.Microsoft;

internal static class ArchitectureChoice
{
    private static readonly string[] Order = ["x64", "arm64", "x86"];

    /// <summary>
    /// The requested architecture as the variant spells it. Without a request the 64-bit PC build is taken when
    /// offered, otherwise the first one.
    /// </summary>
    public static string Choose(CatalogVariant variant, string? requested)
    {
        if (requested is not null)
        {
            return variant.Architectures.FirstOrDefault(a => a.Equals(requested, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"'{variant.Name}' is not offered for {requested}.", nameof(requested));
        }

        if (variant.Architectures.Count == 0)
        {
            throw new ArgumentException($"'{variant.Name}' lists no architecture.", nameof(variant));
        }

        return variant.Architectures.FirstOrDefault(a => a.Equals("x64", StringComparison.OrdinalIgnoreCase)) ?? variant.Architectures[0];
    }

    /// <summary>Lists 64-bit PCs first, then Arm, then 32-bit; unknown names keep their order at the end.</summary>
    public static IReadOnlyList<string> Sort(IEnumerable<string> architectures) =>
        [.. architectures.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(Rank)];

    public static int Rank(string architecture)
    {
        var index = Array.IndexOf(Order, architecture.ToLowerInvariant());
        return index < 0 ? Order.Length : index;
    }
}
