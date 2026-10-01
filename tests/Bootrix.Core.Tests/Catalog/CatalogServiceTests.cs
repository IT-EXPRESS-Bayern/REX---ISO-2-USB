// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Catalog;
using Bootrix.Core.Errors;
using Bootrix.Core.Net;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Core.Tests.Catalog;

public class CatalogServiceTests
{
    private sealed class FakeProvider(string id, params CatalogProduct[] products) : ICatalogProvider
    {
        public bool Throws { get; init; }

        public IReadOnlyList<CatalogVariant> Variants { get; init; } = [];

        public string Id => id;

        public Task<IReadOnlyList<CatalogProduct>> ListProductsAsync(CancellationToken cancellationToken) =>
            Throws ? throw new HttpRequestException("vendor down") : Task.FromResult<IReadOnlyList<CatalogProduct>>(products);

        public Task<IReadOnlyList<CatalogVariant>> ListVariantsAsync(string productId, CancellationToken cancellationToken) =>
            Task.FromResult(Variants);

        public Task<DownloadRequest> ResolveAsync(CatalogVariant variant, string? architecture, CancellationToken cancellationToken) =>
            Task.FromResult(new DownloadRequest(new Uri($"https://{id}.example/{variant.Id}/{architecture}")));
    }

    private static CatalogProduct Product(string provider, string id, string name, CatalogFamily family) =>
        new() { Id = id, Provider = provider, Name = name, Family = family };

    private static CatalogService Service(params ICatalogProvider[] providers) => new(providers, NullLogger<CatalogService>.Instance);

    [Fact]
    public async Task ProductsFromAllProvidersAreSortedByFamilyThenName()
    {
        var service = Service(
            new FakeProvider("a", Product("a", "u", "Ubuntu", CatalogFamily.Linux), Product("a", "d", "Debian", CatalogFamily.Linux)),
            new FakeProvider("b", Product("b", "w", "Windows 11", CatalogFamily.Windows)));

        var listing = await service.ListProductsAsync(CancellationToken.None);

        Assert.Equal(["Windows 11", "Debian", "Ubuntu"], listing.Items.Select(p => p.Name));
        Assert.Empty(listing.Failures);
    }

    [Fact]
    public async Task AFailingProviderIsReportedAndTheOthersStillDeliver()
    {
        var service = Service(
            new FakeProvider("down") { Throws = true },
            new FakeProvider("ok", Product("ok", "u", "Ubuntu", CatalogFamily.Linux)));

        var listing = await service.ListProductsAsync(CancellationToken.None);

        Assert.Single(listing.Items);
        var failure = Assert.Single(listing.Failures);
        Assert.Equal("down", failure.Provider);
        Assert.IsType<HttpRequestException>(failure.Error);
    }

    [Fact]
    public async Task CancellationIsNotSwallowedAsAProviderFailure()
    {
        using var cts = new CancellationTokenSource();
        var service = Service(new CancelingProvider());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ListProductsAsync(cts.Token));
    }

    private sealed class CancelingProvider : ICatalogProvider
    {
        public string Id => "c";

        public Task<IReadOnlyList<CatalogProduct>> ListProductsAsync(CancellationToken cancellationToken) => throw new OperationCanceledException();

        public Task<IReadOnlyList<CatalogVariant>> ListVariantsAsync(string productId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DownloadRequest> ResolveAsync(CatalogVariant variant, string? architecture, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public async Task VariantsListRecommendedFirstThenNewest()
    {
        var provider = new FakeProvider("a", Product("a", "u", "Ubuntu", CatalogFamily.Linux))
        {
            Variants =
            [
                new CatalogVariant { Id = "old", ProductId = "u", Provider = "a", Name = "old", ReleaseDate = new DateOnly(2022, 4, 1) },
                new CatalogVariant { Id = "new", ProductId = "u", Provider = "a", Name = "new", ReleaseDate = new DateOnly(2025, 10, 1) },
                new CatalogVariant { Id = "lts", ProductId = "u", Provider = "a", Name = "lts", ReleaseDate = new DateOnly(2024, 4, 1), IsRecommended = true },
            ],
        };

        var variants = await Service(provider).ListVariantsAsync(Product("a", "u", "Ubuntu", CatalogFamily.Linux), CancellationToken.None);

        Assert.Equal(["lts", "new", "old"], variants.Select(v => v.Id));
    }

    [Fact]
    public async Task ResolveGoesToTheProviderThatOwnsTheVariant()
    {
        var service = Service(new FakeProvider("a"), new FakeProvider("b"));
        var variant = new CatalogVariant { Id = "v1", ProductId = "p", Provider = "B", Name = "x" };

        var request = await service.ResolveAsync(variant, "x64", CancellationToken.None);

        Assert.Equal(new Uri("https://b.example/v1/x64"), request.Url);
    }

    [Fact]
    public async Task UnknownProviderIsACatalogError()
    {
        var variant = new CatalogVariant { Id = "v", ProductId = "p", Provider = "gone", Name = "x" };

        var ex = await Assert.ThrowsAsync<BootrixException>(() => Service(new FakeProvider("a")).ResolveAsync(variant, null, CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, ex.Code);
    }
}
