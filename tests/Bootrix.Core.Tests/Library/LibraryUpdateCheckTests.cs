// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Catalog;
using Bootrix.Core.Library;
using Bootrix.Core.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bootrix.Core.Tests.Library;

public class LibraryUpdateCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    private sealed class StubProvider(Dictionary<string, CatalogVariant[]> variants) : ICatalogProvider
    {
        public bool ProductListingFails { get; init; }

        public HashSet<string> FailingProducts { get; init; } = [];

        public List<string> VariantRequests { get; } = [];

        public string Id => "stub";

        public Task<IReadOnlyList<CatalogProduct>> ListProductsAsync(CancellationToken cancellationToken) =>
            ProductListingFails
                ? throw new HttpRequestException("vendor down")
                : Task.FromResult<IReadOnlyList<CatalogProduct>>([.. variants.Keys.Select(id => new CatalogProduct { Id = id, Provider = Id, Family = CatalogFamily.Linux, Name = id })]);

        public Task<IReadOnlyList<CatalogVariant>> ListVariantsAsync(string productId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VariantRequests.Add(productId);
            return FailingProducts.Contains(productId)
                ? throw new HttpRequestException("vendor down")
                : Task.FromResult<IReadOnlyList<CatalogVariant>>(variants[productId]);
        }

        public Task<DownloadRequest> ResolveAsync(CatalogVariant variant, string? architecture, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static CatalogVariant Variant(string id, string version, string[]? architectures = null, string? language = null, string? endOfSupport = null, bool recommended = false) => new()
    {
        Id = id,
        ProductId = "p",
        Provider = "stub",
        Name = id,
        Version = version,
        Architectures = architectures ?? [],
        Language = language,
        EndOfSupport = endOfSupport is null ? null : DateOnly.Parse(endOfSupport, System.Globalization.CultureInfo.InvariantCulture),
        IsRecommended = recommended,
    };

    private static LibraryEntry Entry(string? product = "p", string? version = "1.0", string? architecture = null, string? language = null, string? variantId = null, char id = 'a') => new()
    {
        Sha256 = new string(id, 64),
        Size = 1,
        Path = "/library/x.iso",
        Location = LibraryLocation.Local,
        Info = new LibraryImageInfo { CatalogId = product, Version = version, Architecture = architecture, Language = language, VariantId = variantId },
        DownloadedUtc = Now,
        LastUsedUtc = Now,
    };

    private static LibraryUpdateCheck Check(StubProvider provider) =>
        new(new CatalogService([provider], NullLogger<CatalogService>.Instance), NullLogger<LibraryUpdateCheck>.Instance, new FakeTimeProvider(Now));

    private static async Task<LibraryUpdateInfo> Single(StubProvider provider, LibraryEntry entry) =>
        Assert.Single(await Check(provider).CheckAsync([entry], CancellationToken.None));

    [Fact]
    public async Task ANewerVersionInTheCatalogIsReported()
    {
        var provider = new StubProvider(new() { ["p"] = [Variant("1.0", "1.0"), Variant("1.1", "1.1"), Variant("2.0", "2.0")] });

        var info = await Single(provider, Entry(version: "1.1"));

        Assert.Equal(LibraryUpdateStatus.NewerAvailable, info.Status);
        Assert.Equal("2.0", info.Newer!.Id);
        Assert.Null(info.SupportEndsOn);
    }

    [Fact]
    public async Task TheNewestVersionIsUpToDate()
    {
        var provider = new StubProvider(new() { ["p"] = [Variant("1.0", "1.0"), Variant("2.0", "2.0")] });

        var info = await Single(provider, Entry(version: "2.0"));

        Assert.Equal(LibraryUpdateStatus.UpToDate, info.Status);
        Assert.Null(info.Newer);
    }

    [Fact]
    public async Task AVersionNewerThanTheCatalogsIsNotCalledOutdated()
    {
        var provider = new StubProvider(new() { ["p"] = [Variant("1.0", "1.0")] });

        Assert.Equal(LibraryUpdateStatus.UpToDate, (await Single(provider, Entry(version: "1.2"))).Status);
    }

    [Fact]
    public async Task OnlyVariantsForTheImagesArchitectureAndLanguageCount()
    {
        var provider = new StubProvider(new()
        {
            ["p"] =
            [
                Variant("1.0-x64", "1.0", ["x64"]),
                Variant("2.0-arm64", "2.0", ["arm64"]),
                Variant("1.5-x64-fr", "1.5", ["x64"], language: "fr"),
                Variant("1.2-any", "1.2"),
            ],
        });

        var x64 = await Single(provider, Entry(version: "1.0", architecture: "X64", language: "de"));
        Assert.Equal("1.2-any", x64.Newer!.Id);

        var arm = await Single(provider, Entry(version: "1.0", architecture: "arm64"));
        Assert.Equal("2.0-arm64", arm.Newer!.Id);

        var french = await Single(provider, Entry(version: "1.0", architecture: "x64", language: "FR"));
        Assert.Equal("1.5-x64-fr", french.Newer!.Id);

        var unspecified = await Single(provider, Entry(version: "1.0"));
        Assert.Equal("2.0-arm64", unspecified.Newer!.Id);
    }

    [Fact]
    public async Task OfTwoVariantsOfTheNewestVersionTheRecommendedOneIsOffered()
    {
        var provider = new StubProvider(new() { ["p"] = [Variant("2.0-server", "2.0"), Variant("2.0-desktop", "2.0", recommended: true)] });

        Assert.Equal("2.0-desktop", (await Single(provider, Entry(version: "1.0"))).Newer!.Id);
    }

    [Fact]
    public async Task EndedSupportIsReportedFromTheStoredVersionsOwnVariant()
    {
        var provider = new StubProvider(new() { ["p"] = [Variant("22.04", "22.04", endOfSupport: "2026-09-30"), Variant("24.04", "24.04", endOfSupport: "2029-04-30")] });

        var old = await Single(provider, Entry(version: "22.04"));
        Assert.Equal(LibraryUpdateStatus.NewerAvailable | LibraryUpdateStatus.SupportEnded, old.Status);
        Assert.Equal(new DateOnly(2026, 9, 30), old.SupportEndsOn);

        var current = await Single(provider, Entry(version: "24.04"));
        Assert.Equal(LibraryUpdateStatus.UpToDate, current.Status);
        Assert.Equal(new DateOnly(2029, 4, 30), current.SupportEndsOn);
    }

    [Fact]
    public async Task SupportEndingTodayHasNotEndedYet()
    {
        var provider = new StubProvider(new() { ["p"] = [Variant("1.0", "1.0", endOfSupport: "2026-10-01")] });
        Assert.Equal(LibraryUpdateStatus.UpToDate, (await Single(provider, Entry())).Status);

        var yesterday = new StubProvider(new() { ["p"] = [Variant("1.0", "1.0", endOfSupport: "2026-09-30")] });
        Assert.Equal(LibraryUpdateStatus.SupportEnded, (await Single(yesterday, Entry())).Status);
    }

    [Fact]
    public async Task TheVariantTheImageCameFromDecidesWhenSeveralShareAVersion()
    {
        var provider = new StubProvider(new()
        {
            ["p"] = [Variant("1.0-desktop", "1.0", endOfSupport: "2030-01-01"), Variant("1.0-legacy", "1.0", endOfSupport: "2020-01-01")],
        });

        Assert.Equal(LibraryUpdateStatus.SupportEnded, (await Single(provider, Entry(variantId: "1.0-legacy"))).Status);
        Assert.Equal(LibraryUpdateStatus.UpToDate, (await Single(provider, Entry(variantId: "1.0-desktop"))).Status);
    }

    [Fact]
    public async Task ImagesWithoutProductOrVersionAndUnknownProductsAreUnchecked()
    {
        var provider = new StubProvider(new() { ["p"] = [Variant("2.0", "2.0")] });

        var infos = await Check(provider).CheckAsync(
            [Entry(product: null, id: 'a'), Entry(version: null, id: 'b'), Entry(product: "unknown", id: 'c'), Entry(id: 'd')],
            CancellationToken.None);

        Assert.Equal(
            [LibraryUpdateStatus.Unchecked, LibraryUpdateStatus.Unchecked, LibraryUpdateStatus.Unchecked, LibraryUpdateStatus.NewerAvailable],
            infos.Select(i => i.Status));
        Assert.Equal(['a', 'b', 'c', 'd'], infos.Select(i => i.Entry.Sha256[0]));
    }

    [Fact]
    public async Task AFailingVendorMakesItsImagesUncheckedAndLeavesTheOthersAlone()
    {
        var provider = new StubProvider(new() { ["p"] = [Variant("2.0", "2.0")], ["q"] = [Variant("3.0", "3.0")] }) { FailingProducts = ["q"] };

        var infos = await Check(provider).CheckAsync([Entry(product: "p", id: 'a'), Entry(product: "q", id: 'b')], CancellationToken.None);

        Assert.Equal([LibraryUpdateStatus.NewerAvailable, LibraryUpdateStatus.Unchecked], infos.Select(i => i.Status));
    }

    [Fact]
    public async Task AnUnreachableCatalogMakesEverythingUncheckedInsteadOfThrowing()
    {
        var provider = new StubProvider(new() { ["p"] = [Variant("2.0", "2.0")] }) { ProductListingFails = true };

        var infos = await Check(provider).CheckAsync([Entry(id: 'a'), Entry(id: 'b')], CancellationToken.None);

        Assert.All(infos, i => Assert.Equal(LibraryUpdateStatus.Unchecked, i.Status));
        Assert.Empty(provider.VariantRequests);
    }

    [Fact]
    public async Task EachProductIsAskedOnceNoMatterHowManyImagesItHas()
    {
        var provider = new StubProvider(new() { ["p"] = [Variant("2.0", "2.0")] });

        await Check(provider).CheckAsync([Entry(version: "1.0", id: 'a'), Entry(version: "1.5", id: 'b'), Entry(version: "2.0", id: 'c')], CancellationToken.None);

        Assert.Equal(["p"], provider.VariantRequests);
    }

    [Fact]
    public async Task NoEntriesNeedNoCatalogRequests()
    {
        var provider = new StubProvider(new() { ["p"] = [Variant("2.0", "2.0")] });

        Assert.Empty(await Check(provider).CheckAsync([], CancellationToken.None));
        Assert.Empty(provider.VariantRequests);
    }

    [Fact]
    public async Task CancellationIsNotSwallowed()
    {
        var provider = new StubProvider(new() { ["p"] = [Variant("2.0", "2.0")] });
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Check(provider).CheckAsync([Entry()], cts.Token));
    }
}
