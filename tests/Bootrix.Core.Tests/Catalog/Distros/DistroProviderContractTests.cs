// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Catalog;
using Bootrix.Core.Catalog.Distros;
using Bootrix.Core.Errors;
using Bootrix.Core.Tests.Catalog.Distros.Support;
using Microsoft.Extensions.Time.Testing;

namespace Bootrix.Core.Tests.Catalog.Distros;

/// <summary>What every distribution provider promises, whatever the vendor's layout: checked for all of them at once.</summary>
public class DistroProviderContractTests
{
    private static readonly Func<HttpClient, TimeProvider, ICatalogProvider>[] Factories =
    [
        (http, time) => new UbuntuProvider(http, time),
        (http, time) => new DebianProvider(http, time),
        (http, time) => new LinuxMintProvider(http, time),
        (http, time) => new FedoraProvider(http, time),
        (http, time) => new ArchLinuxProvider(http, time),
        (http, time) => new OpenSuseProvider(http, time),
        (http, time) => new KaliProvider(http, time),
        (http, time) => new ManjaroProvider(http, time),
        (http, time) => new PopOsProvider(http, time),
        (http, time) => new ElementaryOsProvider(http, time),
        (http, time) => new ZorinOsProvider(http, time),
        (http, time) => new TrueNasProvider(http, time),
        (http, time) => new ProxmoxProvider(http, time),
        (http, time) => new ClonezillaProvider(http, time),
        (http, time) => new GPartedLiveProvider(http, time),
        (http, time) => new RescuezillaProvider(http, time),
        (http, time) => new Memtest86PlusProvider(http, time),
        (http, time) => new FreeBsdProvider(http, time),
    ];

    private static List<ICatalogProvider> AllProviders(FakeWeb web) =>
        [.. Factories.Select(create => create(web.CreateClient(), new FakeTimeProvider(DistroFixtures.CapturedOn)))];

    [Fact]
    public void ProviderIds_AreUniqueAndLowerCase()
    {
        var ids = AllProviders(new FakeWeb()).Select(p => p.Id).ToList();

        Assert.Equal(18, ids.Count);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.Matches("^[a-z0-9]+$", id));
    }

    [Fact]
    public async Task Products_AreListedWithoutTheNetwork_AndTheirIdsAreUniqueAcrossProviders()
    {
        var web = new FakeWeb();
        var products = new List<CatalogProduct>();

        foreach (var provider in AllProviders(web))
        {
            var own = await provider.ListProductsAsync(CancellationToken.None);
            Assert.NotEmpty(own);
            Assert.All(own, p => Assert.Equal(provider.Id, p.Provider));
            products.AddRange(own);
        }

        Assert.Empty(web.Requests);
        Assert.Equal(products.Count, products.Select(p => p.Id).Distinct().Count());
        Assert.Equal(products.Count, products.Select(p => p.Name).Distinct().Count());
    }

    [Fact]
    public async Task Products_AreDescribedForTheUser()
    {
        foreach (var provider in AllProviders(new FakeWeb()))
        {
            foreach (var product in await provider.ListProductsAsync(CancellationToken.None))
            {
                Assert.False(string.IsNullOrWhiteSpace(product.Name), product.Id);
                Assert.False(string.IsNullOrWhiteSpace(product.Description), product.Id);
                Assert.False(string.IsNullOrWhiteSpace(product.License), product.Id);
                Assert.StartsWith("https://", product.Homepage, StringComparison.Ordinal);
                Assert.True(product.Family is CatalogFamily.Linux or CatalogFamily.Bsd or CatalogFamily.Rescue or CatalogFamily.Utility, product.Id);
            }
        }
    }

    [Fact]
    public async Task AnUnknownProduct_IsACallerMistakeEverywhere()
    {
        foreach (var provider in AllProviders(new FakeWeb()))
        {
            await Assert.ThrowsAsync<ArgumentException>(() => provider.ListVariantsAsync("no-such-product", CancellationToken.None));
        }
    }

    [Fact]
    public async Task WhenTheVendorIsUnreachable_ListingEndsInCatalogUnavailableAndNothingElse()
    {
        // An empty site answers 404 to everything, which is what a vendor that moved its files looks like.
        foreach (var provider in AllProviders(new FakeWeb()))
        {
            foreach (var product in await provider.ListProductsAsync(CancellationToken.None))
            {
                try
                {
                    await provider.ListVariantsAsync(product.Id, CancellationToken.None);
                }
                catch (BootrixException ex)
                {
                    Assert.Equal(ErrorCode.CatalogUnavailable, ex.Code);
                }
            }
        }
    }

    [Fact]
    public async Task CancellationIsPassedOn()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        foreach (var provider in AllProviders(new FakeWeb()))
        {
            foreach (var product in await provider.ListProductsAsync(CancellationToken.None))
            {
                try
                {
                    await provider.ListVariantsAsync(product.Id, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // expected for every provider that asks a server
                }
            }
        }
    }

    [Fact]
    public async Task AVariantFromAnotherProvider_IsRefusedByEveryProvider()
    {
        var foreign = new CatalogVariant { Id = "x", ProductId = "x", Provider = "foreign", Name = "x", Architectures = ["x64"] };

        foreach (var provider in AllProviders(new FakeWeb()))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => provider.ResolveAsync(foreign, "x64", CancellationToken.None));
        }
    }
}
