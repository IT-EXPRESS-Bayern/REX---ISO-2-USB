// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Catalog;
using Microsoft.Extensions.Logging;

namespace Bootrix.Core.Library;

/// <summary>
/// Asks the catalog for the variants of each image's product and compares versions. An image only has a counterpart
/// if it recorded its product (<see cref="LibraryImageInfo.CatalogId"/>) and version; images that were added by hand
/// are reported as unchecked rather than guessed about.
/// </summary>
public sealed class LibraryUpdateCheck(CatalogService catalog, ILogger<LibraryUpdateCheck> logger, TimeProvider? time = null) : ILibraryUpdateCheck
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<IReadOnlyList<LibraryUpdateInfo>> CheckAsync(IReadOnlyList<LibraryEntry> entries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var products = (await catalog.ListProductsAsync(cancellationToken).ConfigureAwait(false)).Items
            .GroupBy(p => p.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var variantsByProduct = new Dictionary<string, IReadOnlyList<CatalogVariant>?>(StringComparer.Ordinal);
        foreach (var id in entries.Select(e => e.Info.CatalogId).Where(id => id is not null && products.ContainsKey(id)).Distinct(StringComparer.Ordinal))
        {
            variantsByProduct[id!] = await VariantsOrNullAsync(products[id!], cancellationToken).ConfigureAwait(false);
        }

        var today = DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime);
        return
        [
            .. entries.Select(e =>
            {
                var variants = e.Info.CatalogId is { } id && variantsByProduct.TryGetValue(id, out var found) ? found : null;
                return Compare(e, variants, today);
            }),
        ];
    }

    private async Task<IReadOnlyList<CatalogVariant>?> VariantsOrNullAsync(CatalogProduct product, CancellationToken cancellationToken)
    {
        try
        {
            return await catalog.ListVariantsAsync(product, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The variants of {Product} could not be listed for the update check", product.Id);
            return null;
        }
    }

    private static LibraryUpdateInfo Compare(LibraryEntry entry, IReadOnlyList<CatalogVariant>? variants, DateOnly today)
    {
        if (variants is null || entry.Info.Version is not { } version)
        {
            return new LibraryUpdateInfo(entry, LibraryUpdateStatus.Unchecked, null, null);
        }

        var fitting = variants.Where(v => v.Version is not null && Fits(entry.Info, v)).ToList();
        var status = LibraryUpdateStatus.UpToDate;

        var newest = fitting
            .OrderByDescending(v => v.Version, VersionOrder.Instance)
            .ThenByDescending(v => v.IsRecommended)
            .ThenByDescending(v => v.ReleaseDate)
            .FirstOrDefault();
        var newer = newest is not null && VersionOrder.Instance.Compare(newest.Version, version) > 0 ? newest : null;
        if (newer is not null)
        {
            status |= LibraryUpdateStatus.NewerAvailable;
        }

        // The variant the image came from, found by its own version; one with the same id wins over one that only shares the version.
        var own = fitting
            .Where(v => VersionOrder.Instance.Compare(v.Version, version) == 0)
            .OrderByDescending(v => string.Equals(v.Id, entry.Info.VariantId, StringComparison.Ordinal))
            .FirstOrDefault(v => v.EndOfSupport is not null);

        if (own?.EndOfSupport is { } endsOn && endsOn < today)
        {
            status |= LibraryUpdateStatus.SupportEnded;
        }

        return new LibraryUpdateInfo(entry, status, newer, own?.EndOfSupport);
    }

    /// <summary>A newer image only counts if the user could use it instead: same architecture and language where both sides say.</summary>
    private static bool Fits(LibraryImageInfo info, CatalogVariant variant) =>
        (info.Architecture is null || variant.Architectures.Count == 0 || variant.Architectures.Contains(info.Architecture, StringComparer.OrdinalIgnoreCase))
        && (info.Language is null || variant.Language is null || string.Equals(info.Language, variant.Language, StringComparison.OrdinalIgnoreCase));
}
