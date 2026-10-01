// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bootrix.Core.Errors;
using Bootrix.Core.Net;

namespace Bootrix.Core.Catalog.Rescue;

/// <summary>
/// Offers the rescue catalog through the common <see cref="ICatalogProvider"/> contract. It never talks to a vendor:
/// everything it knows comes from <see cref="RescueCatalogStore"/>. What the contract has no room for (category,
/// write mode, firmware support, notices) is available through <see cref="FindEntry"/> and <see cref="FindVariant"/>.
/// </summary>
public sealed class RescueCatalogProvider : ICatalogProvider
{
    public const string ProviderId = "rescue";

    /// <summary>Product ids carry a prefix because other providers list some of the same tools, such as Clonezilla or Memtest86+.</summary>
    public const string ProductPrefix = "rescue-";

    private readonly RescueCatalogStore _store;
    private readonly CultureInfo? _culture;

    /// <param name="culture">The language of the descriptions; null follows <see cref="CultureInfo.CurrentUICulture"/> at the time of the call.</param>
    public RescueCatalogProvider(RescueCatalogStore store, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        _culture = culture;
    }

    public string Id => ProviderId;

    public RescueCatalogSnapshot Catalog => _store.Current;

    public RescueEntry? FindEntry(string productId)
    {
        if (!productId.StartsWith(ProductPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var id = productId[ProductPrefix.Length..];
        return Catalog.Document.Entries.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal));
    }

    /// <summary>The catalog's full description of a variant that this provider listed, or null if the catalog changed in the meantime.</summary>
    public RescueVariant? FindVariant(CatalogVariant variant)
    {
        ArgumentNullException.ThrowIfNull(variant);

        return FindEntry(variant.ProductId)?.Variants.FirstOrDefault(v => string.Equals(v.Id, variant.Id, StringComparison.Ordinal));
    }

    public Task<IReadOnlyList<CatalogProduct>> ListProductsAsync(CancellationToken cancellationToken)
    {
        var culture = _culture ?? CultureInfo.CurrentUICulture;

        IReadOnlyList<CatalogProduct> products =
        [
            .. Catalog.Document.Entries.Select(e => new CatalogProduct
            {
                Id = ProductPrefix + e.Id,
                Provider = ProviderId,
                Family = FamilyOf(e.Category),
                Name = e.Name,
                Description = e.Description.For(culture),
                Homepage = e.Homepage.AbsoluteUri,
                License = e.License,
            }),
        ];

        return Task.FromResult(products);
    }

    public Task<IReadOnlyList<CatalogVariant>> ListVariantsAsync(string productId, CancellationToken cancellationToken)
    {
        var entry = FindEntry(productId) ?? throw UnknownProduct(productId);

        IReadOnlyList<CatalogVariant> variants =
        [
            .. entry.Variants.Select(v => new CatalogVariant
            {
                Id = v.Id,
                ProductId = productId,
                Provider = ProviderId,
                Name = DisplayName(v),
                Version = v.Version,
                Architectures = v.Architecture is null ? [] : [v.Architecture],
                SizeBytes = v.Size,
                ReleaseDate = v.ReleaseDate,
                EndOfSupport = v.EndOfSupport,
                IsRecommended = v.Recommended,
                ManualUrl = v.ManualUrl?.AbsoluteUri,
            }),
        ];

        return Task.FromResult(variants);
    }

    /// <exception cref="InvalidOperationException">The variant is a manual one; the caller should have sent the user to <see cref="CatalogVariant.ManualUrl"/>.</exception>
    public Task<DownloadRequest> ResolveAsync(CatalogVariant variant, string? architecture, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(variant);

        var entry = FindEntry(variant.ProductId) ?? throw UnknownProduct(variant.ProductId);
        var found = entry.Variants.FirstOrDefault(v => string.Equals(v.Id, variant.Id, StringComparison.Ordinal))
            ?? throw new BootrixException(ErrorCode.CatalogUnavailable, $"'{entry.Name}' has no variant '{variant.Id}' any more");

        if (found.ManualUrl is not null)
        {
            throw new InvalidOperationException($"'{entry.Name}' ({found.Id}) is only offered on the vendor's page, {found.ManualUrl}.");
        }

        if (architecture is not null && found.Architecture is not null && !string.Equals(architecture, found.Architecture, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"'{entry.Name}' ({found.Id}) is built for {found.Architecture}, not {architecture}.", nameof(architecture));
        }

        var request = new DownloadRequest([.. found.Sources.OrderBy(s => s.Priority)])
        {
            ExpectedSize = found.Size,
            ExpectedHashes = found.Sha256 is null ? [] : [new FileHash(HashKind.Sha256, found.Sha256)],
        };

        return Task.FromResult(request);
    }

    private static CatalogFamily FamilyOf(RescueCategory category) => category switch
    {
        RescueCategory.MemoryTest or RescueCategory.DiskDiagnostics or RescueCategory.Partitioning => CatalogFamily.Utility,
        _ => CatalogFamily.Rescue,
    };

    private static string DisplayName(RescueVariant variant)
    {
        var parts = new[] { variant.Version, variant.Architecture, variant.Label }.Where(p => !string.IsNullOrEmpty(p)).ToList();
        return parts.Count > 0 ? string.Join(' ', parts) : variant.Id;
    }

    private static BootrixException UnknownProduct(string productId) =>
        new(ErrorCode.CatalogUnavailable, $"the rescue catalog has no product '{productId}'");
}
