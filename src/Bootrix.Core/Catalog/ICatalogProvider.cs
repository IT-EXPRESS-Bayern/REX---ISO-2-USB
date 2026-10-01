// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Net;

namespace Bootrix.Core.Catalog;

/// <summary>A source of downloadable images. Providers talk to vendor sites; they never write files.</summary>
public interface ICatalogProvider
{
    /// <summary>Stable id such as "microsoft" or "ubuntu"; matches <see cref="CatalogProduct.Provider"/>.</summary>
    string Id { get; }

    Task<IReadOnlyList<CatalogProduct>> ListProductsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<CatalogVariant>> ListVariantsAsync(string productId, CancellationToken cancellationToken);

    /// <summary>
    /// Turns a variant into a ready download: mirrors, expected hashes and size, and a link resolver where the
    /// vendor's addresses expire. <paramref name="architecture"/> is one of the variant's architectures, or null
    /// when it has none or only one. Throws <see cref="Errors.BootrixException"/> with
    /// <c>CatalogUnavailable</c> or <c>DownloadBlocked</c> when the vendor refuses.
    /// </summary>
    Task<DownloadRequest> ResolveAsync(CatalogVariant variant, string? architecture, CancellationToken cancellationToken);
}
