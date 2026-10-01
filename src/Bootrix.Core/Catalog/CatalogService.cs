// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Net;
using Microsoft.Extensions.Logging;

namespace Bootrix.Core.Catalog;

/// <summary>Combines all providers. A provider that fails is reported next to the results of the others, never instead of them.</summary>
public sealed class CatalogService(IEnumerable<ICatalogProvider> providers, ILogger<CatalogService> logger)
{
    private readonly IReadOnlyList<ICatalogProvider> _providers = [.. providers];

    public async Task<CatalogListing<CatalogProduct>> ListProductsAsync(CancellationToken cancellationToken)
    {
        var tasks = _providers.Select(p => Guard(p, () => p.ListProductsAsync(cancellationToken))).ToList();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        return new CatalogListing<CatalogProduct>(
            [.. results.SelectMany(r => r.Items).OrderBy(p => p.Family).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)],
            [.. results.Where(r => r.Failure is not null).Select(r => r.Failure!)]);
    }

    public async Task<IReadOnlyList<CatalogVariant>> ListVariantsAsync(CatalogProduct product, CancellationToken cancellationToken)
    {
        var provider = Find(product.Provider);
        var variants = await provider.ListVariantsAsync(product.Id, cancellationToken).ConfigureAwait(false);
        return [.. variants.OrderByDescending(v => v.IsRecommended).ThenByDescending(v => v.ReleaseDate)];
    }

    public Task<DownloadRequest> ResolveAsync(CatalogVariant variant, string? architecture, CancellationToken cancellationToken) =>
        Find(variant.Provider).ResolveAsync(variant, architecture, cancellationToken);

    private ICatalogProvider Find(string id) =>
        _providers.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
        ?? throw new BootrixException(ErrorCode.CatalogUnavailable, $"unknown provider '{id}'");

    private async Task<(IReadOnlyList<CatalogProduct> Items, ProviderFailure? Failure)> Guard(
        ICatalogProvider provider,
        Func<Task<IReadOnlyList<CatalogProduct>>> list)
    {
        try
        {
            return (await list().ConfigureAwait(false), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Catalog provider {Provider} failed", provider.Id);
            return ([], new ProviderFailure(provider.Id, ex));
        }
    }
}
