// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
using Bootrix.Core.Catalog;
using Bootrix.Core.Catalog.Microsoft;
using Bootrix.Core.Errors;
using Bootrix.Core.Net;
using Bootrix.Core.Tests.Net.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Core.Tests.Catalog.Microsoft;

public class MediaCreationToolProviderTests
{
    private static readonly Uri Windows11Catalog = new("https://go.microsoft.com/fwlink/?LinkId=2156292");
    private static readonly Uri Windows10Catalog = new("https://go.microsoft.com/fwlink/?LinkId=841361");

    private sealed class FixedVerifier(CabSignatureStatus status) : ICabSignatureVerifier
    {
        public CabSignatureResult Verify(ReadOnlyMemory<byte> cabinet) => new(status, "CN=Test");
    }

    private static HttpResponseMessage Serve(HttpRequestMessage request) =>
        request.RequestUri == Windows11Catalog ? Ok("products-win11-24h2.cab")
        : request.RequestUri == Windows10Catalog ? Ok("products-win10-22h2.cab")
        : new HttpResponseMessage(HttpStatusCode.NotFound);

    private static HttpResponseMessage Ok(string fixture) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(MicrosoftFixtures.Bytes(fixture)) };

    private static MediaCreationToolProvider Provider(Func<HttpRequestMessage, HttpResponseMessage> respond, ICabSignatureVerifier? verifier = null) =>
        new(new HttpClient(new StubHandler(respond)), verifier ?? new UnverifiedCabSignatureVerifier(), NullLogger<MediaCreationToolProvider>.Instance);

    private static async Task<CatalogVariant> Variant(MediaCreationToolProvider provider, string product, string id) =>
        Assert.Single(await provider.ListVariantsAsync(product, CancellationToken.None), v => v.Id == id);

    [Fact]
    public async Task ListProducts_OffersBothWindowsGenerationsAsEsdSources()
    {
        var products = await Provider(Serve).ListProductsAsync(CancellationToken.None);

        Assert.Equal(["windows11-mct", "windows10-mct"], products.Select(p => p.Id));
        Assert.All(products, p =>
        {
            Assert.Equal("microsoft-mct", p.Provider);
            Assert.Equal(CatalogFamily.Windows, p.Family);
            Assert.Contains("ESD", p.Name, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task ListVariants_Windows11_YieldsOneVariantPerMediaFamilyAndLanguage()
    {
        var variants = await Provider(Serve).ListVariantsAsync("windows11-mct", CancellationToken.None);

        // 38 languages in two families, plus the one file for the Chinese market.
        Assert.Equal(77, variants.Count);
        Assert.Equal(variants.Count, variants.Select(v => v.Id).Distinct().Count());
        Assert.All(variants, v =>
        {
            Assert.Equal("microsoft-mct", v.Provider);
            Assert.Equal("windows11-mct", v.ProductId);
            Assert.Equal("esd", v.Properties["format"]);
        });
    }

    [Fact]
    public async Task ListVariants_GermanConsumerMedia_CarriesTheRealCatalogData()
    {
        var variant = await Variant(Provider(Serve), "windows11-mct", "consumer/de-de");

        Assert.Equal("Consumer editions - German (Germany)", variant.Name);
        Assert.Equal("de-DE", variant.Language);
        Assert.Equal("24H2 (26100.4349)", variant.Version);
        Assert.Equal(new DateOnly(2025, 6, 7), variant.ReleaseDate);
        Assert.Equal(["x64", "arm64"], variant.Architectures);
        Assert.Equal(4_685_348_140, variant.SizeBytes);
        Assert.Null(variant.EndOfSupport);
        Assert.Null(variant.ManualUrl);
        Assert.Equal("consumer", variant.Properties["channel"]);
        Assert.Contains("Core", variant.Properties["editions"].Split(','));
    }

    [Fact]
    public async Task ListVariants_Windows10_MarksTheEndOfSupportAndOffersThirtyTwoBit()
    {
        var variant = await Variant(Provider(Serve), "windows10-mct", "business/de-de");

        Assert.Equal(new DateOnly(2025, 10, 14), variant.EndOfSupport);
        Assert.Equal("22H2 (19045.3803)", variant.Version);
        Assert.Equal(["x64", "arm64", "x86"], variant.Architectures);
    }

    [Fact]
    public async Task ListVariants_ChineseMarketMedia_HasOnlyTheArchitecturesTheCatalogLists()
    {
        var variant = await Variant(Provider(Serve), "windows11-mct", "china/zh-cn");

        Assert.Equal(["x64"], variant.Architectures);
        Assert.Equal("zh-CN", variant.Language);
    }

    [Fact]
    public async Task Resolve_ReturnsTheEsdWithItsSha1AndSizeFromTheCatalog()
    {
        var provider = Provider(Serve);
        var variant = await Variant(provider, "windows11-mct", "consumer/de-de");

        var request = await provider.ResolveAsync(variant, "x64", CancellationToken.None);

        // The delivery host answers on plain HTTP only; the digest from the catalog is what protects the file.
        Assert.Equal(
            "http://dl.delivery.mp.microsoft.com/filestreamingservice/files/1094408c-5ad0-4d3c-aaa0-db1e353ec0d2/26100.4349.250607-1500.ge_release_svc_refresh_CLIENTCONSUMER_RET_x64FRE_de-de.esd",
            request.Url.AbsoluteUri);
        Assert.Equal([new FileHash(HashKind.Sha1, "f1f75016fdee2d81d220be4ac2876f6f564e780f")], request.ExpectedHashes);
        Assert.Equal(4_685_348_140, request.ExpectedSize);
        Assert.Null(request.LinkResolver);

        // SHA-1 from the catalog is accepted as an integrity check by the downloader; only MD5 is not.
        request.Validate();
    }

    [Fact]
    public async Task Resolve_Arm64_PicksTheOtherFile()
    {
        var provider = Provider(Serve);
        var variant = await Variant(provider, "windows11-mct", "consumer/de-de");

        var request = await provider.ResolveAsync(variant, "ARM64", CancellationToken.None);

        Assert.EndsWith("CLIENTCONSUMER_RET_A64FRE_de-de.esd", request.Url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("cd79755fe0c0fa26279959bc2296739e3c21b722", request.ExpectedHashes.Single().Hex);
        Assert.Equal(4_540_927_577, request.ExpectedSize);
    }

    [Fact]
    public async Task Resolve_WithoutArchitecture_TakesTheSixtyFourBitPc()
    {
        var provider = Provider(Serve);
        var variant = await Variant(provider, "windows10-mct", "consumer/de-de");

        var request = await provider.ResolveAsync(variant, null, CancellationToken.None);

        Assert.Contains("_x64FRE_", request.Url.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_ArchitectureThatIsNotOffered_IsRejected()
    {
        var provider = Provider(Serve);
        var variant = await Variant(provider, "windows11-mct", "consumer/de-de");

        await Assert.ThrowsAsync<ArgumentException>(() => provider.ResolveAsync(variant, "x86", CancellationToken.None));
    }

    [Fact]
    public async Task Resolve_VariantOfAnotherProvider_IsRejected()
    {
        var foreign = new CatalogVariant
        {
            Id = "x",
            ProductId = "ubuntu",
            Provider = "ubuntu",
            Name = "Ubuntu",
            Architectures = ["x64"],
        };

        await Assert.ThrowsAsync<ArgumentException>(() => Provider(Serve).ResolveAsync(foreign, "x64", CancellationToken.None));
    }

    [Fact]
    public async Task ListVariants_UnknownProduct_IsRejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Provider(Serve).ListVariantsAsync("windows7", CancellationToken.None));
    }

    [Theory]
    [InlineData(CabSignatureStatus.NotChecked, "not-checked")]
    [InlineData(CabSignatureStatus.NotSigned, "not-signed")]
    [InlineData(CabSignatureStatus.Valid, "valid")]
    public async Task ListVariants_ReportsWhatWasKnownAboutTheCatalogSignature(CabSignatureStatus status, string expected)
    {
        var variant = await Variant(Provider(Serve, new FixedVerifier(status)), "windows11-mct", "consumer/de-de");

        Assert.Equal(expected, variant.Properties["catalogSignature"]);
    }

    [Fact]
    public async Task ListVariants_InvalidCatalogSignature_StopsWithSignatureInvalid()
    {
        var provider = Provider(Serve, new FixedVerifier(CabSignatureStatus.Invalid));

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ListVariantsAsync("windows11-mct", CancellationToken.None));

        Assert.Equal(ErrorCode.SignatureInvalid, error.Code);
    }

    [Fact]
    public async Task ListVariants_SendsAnHonestUserAgent()
    {
        var handler = new StubHandler(Serve);
        var provider = new MediaCreationToolProvider(new HttpClient(handler), NullLogger<MediaCreationToolProvider>.Instance);

        await provider.ListVariantsAsync("windows11-mct", CancellationToken.None);

        var agent = Assert.Single(handler.Calls).Request.Headers.UserAgent.ToString();
        Assert.StartsWith("Bootrix/", agent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListVariants_FollowsRedirectsEvenIfTheClientDoesNot()
    {
        var provider = Provider(request => request.RequestUri == Windows11Catalog
            ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://download.microsoft.com/download/x/products.cab") } }
            : request.RequestUri!.Host == "download.microsoft.com" ? Ok("products-win11-24h2.cab") : new HttpResponseMessage(HttpStatusCode.NotFound));

        Assert.Equal(77, (await provider.ListVariantsAsync("windows11-mct", CancellationToken.None)).Count);
    }

    [Fact]
    public async Task ListVariants_RedirectToPlainHttp_IsRefused()
    {
        var provider = Provider(_ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("http://download.microsoft.com/products.cab") } });

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ListVariantsAsync("windows11-mct", CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
        Assert.Contains("not https", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListVariants_EndlessRedirects_AreCutOff()
    {
        var provider = Provider(_ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("https://go.microsoft.com/again") } });

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ListVariantsAsync("windows11-mct", CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
    }

    [Fact]
    public async Task ListVariants_ServerError_BecomesCatalogUnavailableWithTheStatus()
    {
        var provider = Provider(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ListVariantsAsync("windows11-mct", CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
        Assert.Contains("HTTP 404", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListVariants_RateLimit_NamesTheWaitTheServerAsksFor()
    {
        var provider = Provider(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(90));
            return response;
        });

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ListVariantsAsync("windows10-mct", CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
        Assert.Contains("HTTP 429", error.Detail, StringComparison.Ordinal);
        Assert.Contains("retry after 90 s", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListVariants_ConnectionFailure_BecomesCatalogUnavailable()
    {
        var provider = Provider(_ => throw new HttpRequestException("connection refused"));

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ListVariantsAsync("windows11-mct", CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
        Assert.IsType<HttpRequestException>(error.InnerException);
    }

    [Fact]
    public async Task ListVariants_NoAnswerInTime_BecomesCatalogUnavailableNotCancellation()
    {
        // HttpClient reports its own timeout as a cancelled task; the caller did not cancel anything.
        var provider = Provider(_ => throw new TaskCanceledException("The request was canceled due to the configured timeout."));

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ListVariantsAsync("windows11-mct", CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
        Assert.Contains("did not answer within 30 s", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListVariants_Cancellation_IsNotReportedAsUnavailable()
    {
        var provider = Provider(Serve);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ListVariantsAsync("windows11-mct", cts.Token));
    }

    [Fact]
    public async Task ListVariants_AnswerThatIsNoCabinet_BecomesCatalogUnavailable()
    {
        var provider = Provider(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>Access denied</html>") });

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ListVariantsAsync("windows11-mct", CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
        Assert.Contains("cannot be read", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListVariants_CabinetWithoutProductsXml_BecomesCatalogUnavailable()
    {
        var cabinet = new CabinetBuilder().Folder(CabinetBuilder.Stored, ("x"u8.ToArray(), 1)).File("other.xml", 0, 0, 1).Build();
        var provider = Provider(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(cabinet) });

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ListVariantsAsync("windows11-mct", CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
        Assert.Contains("products.xml", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListVariants_CabinetWithAnUnusableCatalog_BecomesCatalogUnavailable()
    {
        var xml = "<MCT><Catalogs/></MCT>"u8.ToArray();
        var cabinet = new CabinetBuilder().Folder(CabinetBuilder.Stored, (xml, xml.Length)).File("products.xml", 0, 0, (uint)xml.Length).Build();
        var provider = Provider(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(cabinet) });

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ListVariantsAsync("windows11-mct", CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
    }

    [Fact]
    public async Task ListVariants_OversizedAnswer_IsRefused()
    {
        var provider = Provider(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[9 * 1024 * 1024]) });

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ListVariantsAsync("windows11-mct", CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
    }

    [Fact]
    public async Task CatalogService_CombinesTheFallbackWithOtherProviders()
    {
        var provider = Provider(Serve);
        var service = new CatalogService([provider], NullLogger<CatalogService>.Instance);

        var listing = await service.ListProductsAsync(CancellationToken.None);
        var variants = await service.ListVariantsAsync(listing.Items.First(p => p.Id == "windows11-mct"), CancellationToken.None);
        var request = await service.ResolveAsync(variants.First(v => v.Id == "consumer/de-de"), "x64", CancellationToken.None);

        Assert.Empty(listing.Failures);
        Assert.Contains("x64FRE_de-de.esd", request.Url.AbsoluteUri, StringComparison.Ordinal);
    }
}
