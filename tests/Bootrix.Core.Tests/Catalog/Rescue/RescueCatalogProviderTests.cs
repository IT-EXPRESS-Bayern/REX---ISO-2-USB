// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bootrix.Core.Catalog;
using Bootrix.Core.Catalog.Rescue;
using Bootrix.Core.Errors;
using Bootrix.Core.Net;
using Bootrix.Core.Tests.Net.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Core.Tests.Catalog.Rescue;

public sealed class RescueCatalogProviderTests : IDisposable
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    private readonly ManifestSigner _signer = new("rescue-a");
    private readonly TempDirectory _cache = new();

    public void Dispose()
    {
        _signer.Dispose();
        _cache.Dispose();
    }

    private RescueCatalogProvider Provider(CultureInfo? culture = null) => new(
        new RescueCatalogStore(_cache.Path, new SignedManifestVerifier([_signer.PublicKey], new InMemoryVersionStore()), NullLogger<RescueCatalogStore>.Instance),
        culture);

    private static async Task<(RescueCatalogProvider Provider, CatalogProduct Product, CatalogVariant Variant)> Pick(RescueCatalogProvider provider, string entry, string? variantId = null)
    {
        var product = (await provider.ListProductsAsync(CancellationToken.None)).Single(p => p.Id == RescueCatalogProvider.ProductPrefix + entry);
        var variants = await provider.ListVariantsAsync(product.Id, CancellationToken.None);
        return (provider, product, variantId is null ? variants[0] : variants.Single(v => v.Id == variantId));
    }

    [Fact]
    public async Task ListsEveryEntryAsAProductOfThisProvider()
    {
        var provider = Provider(English);

        var products = await provider.ListProductsAsync(CancellationToken.None);

        Assert.Equal(provider.Catalog.Document.Entries.Count, products.Count);
        Assert.All(products, p =>
        {
            Assert.Equal("rescue", p.Provider);
            Assert.StartsWith("rescue-", p.Id, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(p.Description));
            Assert.StartsWith("https://", p.Homepage, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(p.License));
        });
        Assert.Equal(products.Count, products.Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public async Task ProductIdsDoNotCollideWithTheNamesOtherProvidersUse()
    {
        var products = await Provider().ListProductsAsync(CancellationToken.None);

        Assert.DoesNotContain(products, p => p.Id is "clonezilla" or "gparted" or "rescuezilla" or "memtest86plus");
        Assert.Contains(products, p => p.Id == "rescue-clonezilla");
    }

    [Fact]
    public async Task DescriptionsFollowTheLanguageOfTheProvider()
    {
        var german = (await Provider(German).ListProductsAsync(CancellationToken.None)).Single(p => p.Id == "rescue-gparted-live");
        var english = (await Provider(English).ListProductsAsync(CancellationToken.None)).Single(p => p.Id == "rescue-gparted-live");

        Assert.StartsWith("Startfähiges Abbild", german.Description, StringComparison.Ordinal);
        Assert.StartsWith("Bootable image", english.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutALanguageTheCurrentUiCultureDecides()
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = German;
            var german = (await Provider().ListProductsAsync(CancellationToken.None)).Single(p => p.Id == "rescue-gparted-live");
            CultureInfo.CurrentUICulture = English;
            var english = (await Provider().ListProductsAsync(CancellationToken.None)).Single(p => p.Id == "rescue-gparted-live");

            Assert.NotEqual(german.Description, english.Description);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Theory]
    [InlineData("systemrescue", CatalogFamily.Rescue)]
    [InlineData("hirens-bootcd-pe", CatalogFamily.Rescue)]
    [InlineData("chntpw", CatalogFamily.Rescue)]
    [InlineData("memtest86plus", CatalogFamily.Utility)]
    [InlineData("gparted-live", CatalogFamily.Utility)]
    [InlineData("seatools", CatalogFamily.Utility)]
    public async Task FamilyFollowsTheCategory(string entry, CatalogFamily family)
    {
        var (_, product, _) = await Pick(Provider(), entry);

        Assert.Equal(family, product.Family);
    }

    [Fact]
    public async Task VariantsCarryWhatTheContractHasRoomFor()
    {
        var (_, product, variant) = await Pick(Provider(), "systemrescue");

        Assert.Equal("13.02-amd64", variant.Id);
        Assert.Equal(product.Id, variant.ProductId);
        Assert.Equal("rescue", variant.Provider);
        Assert.Equal("13.02 x64", variant.Name);
        Assert.Equal("13.02", variant.Version);
        Assert.Equal(["x64"], variant.Architectures);
        Assert.Equal(1381629952, variant.SizeBytes);
        Assert.Equal(new DateOnly(2026, 8, 1), variant.ReleaseDate);
        Assert.True(variant.IsRecommended);
        Assert.Null(variant.ManualUrl);
    }

    [Fact]
    public async Task VariantNamesTellTheSiblingsApart()
    {
        var names = (await Provider().ListVariantsAsync("rescue-super-grub2-disk", CancellationToken.None)).Select(v => v.Name);

        Assert.Equal(["2.06s4 USB image, Secure Boot", "2.06s4 CD image, classic"], names);
    }

    [Fact]
    public async Task ManualVariantsAreMarkedWithTheirAddress()
    {
        var (_, _, variant) = await Pick(Provider(), "kaspersky-rescue-disk");

        Assert.Equal("https://www.kaspersky.com/downloads/free-rescue-disk", variant.ManualUrl);
    }

    [Fact]
    public async Task ResolvingGivesTheMirrorsInPriorityOrderWithSizeAndDigest()
    {
        var (provider, _, variant) = await Pick(Provider(), "systemrescue");

        var request = await provider.ResolveAsync(variant, "x64", CancellationToken.None);

        Assert.Equal(
            [
                "https://fastly-cdn.system-rescue.org/releases/13.02/systemrescue-13.02-amd64.iso",
                "https://sourceforge.net/projects/systemrescuecd/files/sysresccd-x86/13.02/systemrescue-13.02-amd64.iso/download",
            ],
            request.Sources.Select(s => s.Url.AbsoluteUri));
        Assert.Equal(1381629952, request.ExpectedSize);
        Assert.Equal(new FileHash(HashKind.Sha256, "ad4d670b72859d887c7960142a9a9d36a3e50446694a035e254442f65d6e7572"), Assert.Single(request.ExpectedHashes));
    }

    [Fact]
    public async Task ResolvingAVariantWithoutADigestExpectsNoneAndAllowsOmittingTheArchitecture()
    {
        var (provider, _, variant) = await Pick(Provider(), "drweb-livedisk");

        var request = await provider.ResolveAsync(variant, null, CancellationToken.None);

        Assert.Empty(request.ExpectedHashes);
        Assert.Null(request.ExpectedSize);
        Assert.Equal("https://cdn-download.drweb.com/pub/drweb/livedisk/drweb-livedisk-900-cd.iso", request.Url.AbsoluteUri);
    }

    [Fact]
    public async Task EveryDownloadableVariantOfTheCatalogResolvesToARequestTheDownloaderAccepts()
    {
        var provider = Provider();
        var resolved = 0;

        foreach (var product in await provider.ListProductsAsync(CancellationToken.None))
        {
            foreach (var variant in await provider.ListVariantsAsync(product.Id, CancellationToken.None))
            {
                if (variant.ManualUrl is not null)
                {
                    continue;
                }

                var request = await provider.ResolveAsync(variant, variant.Architectures.Count > 0 ? variant.Architectures[0] : null, CancellationToken.None);
                request.Validate();
                resolved++;
            }
        }

        Assert.True(resolved >= 15);
    }

    [Fact]
    public async Task ResolvingAManualVariantIsAMistakeOfTheCaller()
    {
        var (provider, _, variant) = await Pick(Provider(), "medicat-usb");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ResolveAsync(variant, null, CancellationToken.None));

        Assert.Contains("https://medicatusb.com/", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolvingForTheWrongArchitectureIsRefused()
    {
        var (provider, _, variant) = await Pick(Provider(), "memtest86plus", "8.10-x86_64");

        await Assert.ThrowsAsync<ArgumentException>(() => provider.ResolveAsync(variant, "arm64", CancellationToken.None));
        Assert.NotNull(await provider.ResolveAsync(variant, "X64", CancellationToken.None));
    }

    [Fact]
    public async Task UnknownProductsAndVariantsAreCatalogProblems()
    {
        var provider = Provider();
        var (_, _, variant) = await Pick(provider, "systemrescue");

        var product = await Assert.ThrowsAsync<BootrixException>(() => provider.ListVariantsAsync("rescue-nothing", CancellationToken.None));
        var foreign = await Assert.ThrowsAsync<BootrixException>(() => provider.ListVariantsAsync("clonezilla", CancellationToken.None));
        var gone = await Assert.ThrowsAsync<BootrixException>(() => provider.ResolveAsync(variant with { Id = "0.1" }, null, CancellationToken.None));

        Assert.All([product, foreign, gone], ex => Assert.Equal(ErrorCode.CatalogUnavailable, ex.Code));
    }

    [Fact]
    public async Task TheFullEntryIsAvailableForWhatTheContractCannotCarry()
    {
        var provider = Provider();
        var (_, product, variant) = await Pick(provider, "chntpw");

        var entry = provider.FindEntry(product.Id)!;
        var detail = provider.FindVariant(variant)!;

        Assert.Equal(RescueCategory.OfflinePasswordReset, entry.Category);
        Assert.Contains(RescueNotices.AuthorizedUseOnly, entry.Notices);
        Assert.Equal(RescueWriteMode.IsoExtract, detail.WriteMode);
        Assert.Equal(RescuePackaging.Zip, detail.Packaging);
        Assert.Equal(RescueHashOrigin.Pinned, detail.HashOrigin);
        Assert.Null(provider.FindEntry("chntpw"));
        Assert.Null(provider.FindEntry("rescue-unknown"));
        Assert.Null(provider.FindVariant(variant with { Id = "other" }));
    }

    [Fact]
    public async Task WorksBehindTheCatalogServiceWithOtherProviders()
    {
        var service = new CatalogService([Provider(English)], NullLogger<CatalogService>.Instance);

        var listing = await service.ListProductsAsync(CancellationToken.None);
        var memtest = listing.Items.Single(p => p.Id == "rescue-memtest86plus");
        var variants = await service.ListVariantsAsync(memtest, CancellationToken.None);

        Assert.Empty(listing.Failures);
        Assert.Equal("8.10-x86_64", variants[0].Id);
        Assert.Equal("https://www.memtest.org/download/v8.10/mt86plus_8.10_x86_64.iso.zip", (await service.ResolveAsync(variants[0], "x64", CancellationToken.None)).Url.AbsoluteUri);
    }
}
