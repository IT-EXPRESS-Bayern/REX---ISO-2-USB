// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Catalog.Rescue;

public enum RescueCatalogOrigin
{
    /// <summary>The catalog that shipped with this build.</summary>
    Embedded,

    /// <summary>A newer catalog from the signed channel, kept in the cache folder.</summary>
    Cache,
}

/// <summary>The catalog that is in effect, and where it came from.</summary>
/// <param name="IsStale">The catalog's own expiry date has passed. It is still used, because old links are better than none, but the UI should say so and offer an update.</param>
public sealed record RescueCatalogSnapshot(RescueCatalogDocument Document, RescueCatalogOrigin Origin, bool IsStale);
