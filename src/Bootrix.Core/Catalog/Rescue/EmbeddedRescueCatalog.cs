// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Catalog.Rescue;

internal static class EmbeddedRescueCatalog
{
    private const string ResourceName = "Bootrix.Core.Catalog.Rescue.rescue-catalog.json";

    private static readonly Lazy<RescueCatalogDocument> Parsed = new(Load);

    public static RescueCatalogDocument Document => Parsed.Value;

    internal static RescueCatalogDocument Load()
    {
        using var stream = typeof(EmbeddedRescueCatalog).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw CatalogRules.Invalid("the embedded catalog is missing from the assembly");
        return RescueCatalogReader.Read(stream);
    }
}
