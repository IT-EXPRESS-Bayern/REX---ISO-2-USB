// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using Bootrix.Core.Catalog;
using Bootrix.Core.Catalog.Microsoft;
using Bootrix.Core.Errors;
using Bootrix.Core.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bootrix.Core.Tests.Catalog.Microsoft;

public class MicrosoftIsoProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 7, 0, 0, TimeSpan.Zero);

    private const string GermanWindows11X64Sha256 = "193bbde65ec84e298a1c798959489c8375ed501cf973c6d37fa52bb22ee8d43b";

    private static MicrosoftIsoProvider Provider(FakeMicrosoftServer server) =>
        new(server.Client(), NullLogger<MicrosoftIsoProvider>.Instance, new FakeTimeProvider(Now));

    private static async Task<CatalogVariant> German(MicrosoftIsoProvider provider, string product = "windows11") =>
        Assert.Single(await provider.ListVariantsAsync(product, CancellationToken.None), v => v.Language == "de-DE");

    private static HttpResponseMessage Status(HttpStatusCode status) => new(status);

    [Fact]
    public async Task ListProducts_OffersWindows11AndWindows10WithTheEndOfSupportNamed()
    {
        var products = await Provider(new FakeMicrosoftServer()).ListProductsAsync(CancellationToken.None);

        Assert.Equal(["windows11", "windows10"], products.Select(p => p.Id));
        Assert.All(products, p =>
        {
            Assert.Equal("microsoft", p.Provider);
            Assert.Equal(CatalogFamily.Windows, p.Family);
            Assert.StartsWith("https://www.microsoft.com/software-download/", p.Homepage, StringComparison.Ordinal);
        });

        var windows10 = products.Single(p => p.Id == "windows10");
        Assert.Contains("22H2", windows10.Description, StringComparison.Ordinal);
        Assert.Contains("14 October 2025", windows10.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListVariants_Windows11_MergesTheX64AndArm64PagesPerLanguage()
    {
        var server = new FakeMicrosoftServer();

        var variants = await Provider(server).ListVariantsAsync("windows11", CancellationToken.None);

        Assert.Equal(38, variants.Count);
        Assert.Equal(variants.Count, variants.Select(v => v.Id).Distinct().Count());

        var german = Assert.Single(variants, v => v.Id == "de-DE");
        Assert.Equal("German", german.Name);
        Assert.Equal("de-DE", german.Language);
        Assert.Equal("26H2 (26300.9457)", german.Version);
        Assert.Equal(["x64", "arm64"], german.Architectures);
        Assert.Equal("windows11", german.ProductId);
        Assert.Equal("microsoft", german.Provider);
        Assert.Null(german.EndOfSupport);
        Assert.Null(german.ManualUrl);
        Assert.Equal("iso", german.Properties["format"]);
        Assert.Equal("German", german.Properties["language"]);
        Assert.Equal("windows11|3813", german.Properties["route.x64"]);
        Assert.Equal("windows11arm64|3816", german.Properties["route.arm64"]);
    }

    [Fact]
    public async Task ListVariants_Windows11_MapsLanguageNamesToTags()
    {
        var variants = await Provider(new FakeMicrosoftServer()).ListVariantsAsync("windows11", CancellationToken.None);

        Assert.Equal("en-GB", Assert.Single(variants, v => v.Name == "English (United Kingdom)").Language);
        Assert.Equal("en-US", Assert.Single(variants, v => v.Name == "English").Language);
        Assert.Equal("zh-CN", Assert.Single(variants, v => v.Name == "Chinese (Simplified)").Language);
        Assert.Equal("sr-Latn-RS", Assert.Single(variants, v => v.Name == "Serbian Latin").Language);
        Assert.Equal("pt-BR", Assert.Single(variants, v => v.Name == "Brazilian Portuguese").Language);
    }

    [Fact]
    public async Task ListVariants_Windows10_OffersBothWordSizesAndMarksTheEndOfSupport()
    {
        var variants = await Provider(new FakeMicrosoftServer()).ListVariantsAsync("windows10", CancellationToken.None);

        Assert.Equal(38, variants.Count);
        var german = Assert.Single(variants, v => v.Id == "de-DE");
        Assert.Equal(["x64", "x86"], german.Architectures);
        Assert.Equal("22H2", german.Version);
        Assert.Equal(new DateOnly(2025, 10, 14), german.EndOfSupport);
        Assert.Equal("windows10ISO|2618", german.Properties["route.x64"]);
        Assert.Equal("windows10ISO|2618", german.Properties["route.x86"]);

        // Windows 10 calls it "English International", Windows 11 "English (United Kingdom)"; both are en-GB.
        Assert.Equal("English International", Assert.Single(variants, v => v.Id == "en-GB").Name);
    }

    [Fact]
    public async Task ListVariants_NeedsNoHandshakeWhileTheServiceAnswersWithoutOne()
    {
        var server = new FakeMicrosoftServer();

        await Provider(server).ListVariantsAsync("windows11", CancellationToken.None);

        Assert.DoesNotContain(server.Urls, u => u.Contains("ov-df", StringComparison.Ordinal) || u.Contains("vlscppe", StringComparison.Ordinal));
        Assert.Equal(2, server.Api("getskuinformationbyproductedition").Count());
    }

    [Fact]
    public async Task ListVariants_ServiceRejectsTheListUntilThePing_RepeatsItAfterTheHandshake()
    {
        var server = new FakeMicrosoftServer { SkusNeedHandshake = true };

        var variants = await Provider(server).ListVariantsAsync("windows10", CancellationToken.None);

        Assert.Equal(38, variants.Count);
        Assert.Equal(2, server.Api("getskuinformationbyproductedition").Count());
        Assert.Single(server.Handshaken);
    }

    [Fact]
    public async Task ListVariants_OneArchitecturePageBroken_StillListsTheOther()
    {
        var server = new FakeMicrosoftServer
        {
            Override = r => r.RequestUri!.AbsolutePath.EndsWith("/windows11arm64", StringComparison.Ordinal) ? FakeMicrosoftServer.Html("<html>redesigned</html>") : null,
        };

        var variants = await Provider(server).ListVariantsAsync("windows11", CancellationToken.None);

        Assert.Equal(38, variants.Count);
        Assert.All(variants, v => Assert.Equal(["x64"], v.Architectures));
        Assert.DoesNotContain(variants, v => v.Properties.ContainsKey("route.arm64"));
    }

    [Fact]
    public async Task ListVariants_PageLayoutChanged_ReportsCatalogUnavailableWithTheTechnicalReason()
    {
        var server = new FakeMicrosoftServer { Override = r => r.RequestUri!.Host == "www.microsoft.com" && r.RequestUri.AbsolutePath.Contains("software-download/", StringComparison.Ordinal) ? FakeMicrosoftServer.Html("<html><body>Welcome</body></html>") : null };

        var error = await Assert.ThrowsAsync<BootrixException>(() => Provider(server).ListVariantsAsync("windows10", CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
        Assert.Contains("unexpected structure", error.Detail, StringComparison.Ordinal);
        Assert.Contains("windows10ISO", error.Detail, StringComparison.Ordinal);
        Assert.Contains("product-edition", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListVariants_EditionWithoutLanguages_IsCatalogUnavailable()
    {
        var server = new FakeMicrosoftServer
        {
            Override = r => r.RequestUri!.AbsolutePath.EndsWith("/getskuinformationbyproductedition", StringComparison.Ordinal)
                ? FakeMicrosoftServer.Json("""{"Skus":[],"ValidationContainer":{"Errors":[]}}""")
                : null,
        };

        var error = await Assert.ThrowsAsync<BootrixException>(() => Provider(server).ListVariantsAsync("windows10", CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
        Assert.Contains("lists no language", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListVariants_ServiceAnswersWithUnknownError_ReportsItsKey()
    {
        var server = new FakeMicrosoftServer
        {
            Override = r => r.RequestUri!.AbsolutePath.EndsWith("/getskuinformationbyproductedition", StringComparison.Ordinal)
                ? FakeMicrosoftServer.Json("""{"Errors":[{"Key":"ErrorSettings.GenericError","Value":"Try later","Type":1}]}""")
                : null,
        };

        var error = await Assert.ThrowsAsync<BootrixException>(() => Provider(server).ListVariantsAsync("windows10", CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
        Assert.IsNotType<MicrosoftDownloadBlockedException>(error);
        Assert.Contains("ErrorSettings.GenericError: Try later", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListVariants_RateLimit_NamesTheWait()
    {
        var server = new FakeMicrosoftServer
        {
            Override = r =>
            {
                var response = Status(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
                return r.RequestUri!.AbsolutePath.EndsWith("/getskuinformationbyproductedition", StringComparison.Ordinal) ? response : null;
            },
        };

        var error = await Assert.ThrowsAsync<BootrixException>(() => Provider(server).ListVariantsAsync("windows10", CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
        Assert.Contains("HTTP 429", error.Detail, StringComparison.Ordinal);
        Assert.Contains("retry after 120 s", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListVariants_AnswerThatIsNoJson_ReportsCatalogUnavailable()
    {
        var server = new FakeMicrosoftServer
        {
            Override = r => r.RequestUri!.AbsolutePath.EndsWith("/getskuinformationbyproductedition", StringComparison.Ordinal)
                ? FakeMicrosoftServer.Html("<html>Service unavailable</html>")
                : null,
        };

        var error = await Assert.ThrowsAsync<BootrixException>(() => Provider(server).ListVariantsAsync("windows10", CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
        Assert.Contains("Unexpected answer", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListVariants_PageForbidden_IsReportedAsBlockedWithTheBrowserAddress()
    {
        var server = new FakeMicrosoftServer { Override = r => r.RequestUri!.Host == "www.microsoft.com" ? Status(HttpStatusCode.Forbidden) : null };

        var error = await Assert.ThrowsAsync<MicrosoftDownloadBlockedException>(() => Provider(server).ListVariantsAsync("windows10", CancellationToken.None));

        Assert.Equal(ErrorCode.DownloadBlocked, error.Code);
        Assert.Equal("HTTP 403", error.MessageCode);
        Assert.Equal("https://www.microsoft.com/software-download/windows10ISO", error.ManualUrl.AbsoluteUri);
    }

    [Fact]
    public async Task ListVariants_ConnectionFailure_BecomesCatalogUnavailable()
    {
        var server = new FakeMicrosoftServer { Override = _ => throw new HttpRequestException("name resolution failed") };

        var error = await Assert.ThrowsAsync<BootrixException>(() => Provider(server).ListVariantsAsync("windows11", CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
        Assert.Contains("name resolution failed", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListVariants_Cancellation_IsPassedOn()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider(new FakeMicrosoftServer()).ListVariantsAsync("windows11", cts.Token));
    }

    [Fact]
    public async Task ListVariants_UnknownProduct_IsRejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Provider(new FakeMicrosoftServer()).ListVariantsAsync("windows7", CancellationToken.None));
    }

    [Fact]
    public async Task Resolve_GermanWindows11X64_RunsTheHandshakeBeforeAskingForLinks()
    {
        var server = new FakeMicrosoftServer();
        var provider = Provider(server);
        var variant = await German(provider);
        server.Calls.Clear();

        var request = await provider.ResolveAsync(variant, "x64", CancellationToken.None);

        Assert.Equal(
            "https://software.download.prss.microsoft.com/dbazure/Windows11_Client_x64_de-de_26300_9457.iso?t=00000001-0000-0000-0000-000000000000&P1=1790924835&P2=602&P3=2&P4=SIGNATURE-PLACEHOLDER",
            request.Url.AbsoluteUri);
        Assert.Equal([new FileHash(HashKind.Sha256, GermanWindows11X64Sha256)], request.ExpectedHashes);
        Assert.Null(request.ExpectedSize);
        Assert.NotNull(request.LinkResolver);

        // Page, then the script, its ping, the language list and the link request, all of one session.
        var calls = server.Calls.Select(c => $"{c.RequestUri!.Host}{c.RequestUri.AbsolutePath}").ToList();
        Assert.Equal(
            [
                "www.microsoft.com/en-us/software-download/windows11",
                "www.microsoft.com/software-download-connector/api/getskuinformationbyproductedition",
                "ov-df.microsoft.com/mdt.js",
                "ov-df.microsoft.com/",
                "www.microsoft.com/software-download-connector/api/GetProductDownloadLinksBySku",
            ],
            calls);

        var session = FakeMicrosoftServer.Query(server.Api("GetProductDownloadLinksBySku").Single(), "sessionID");
        Assert.True(Guid.TryParse(session, out _));
        Assert.Contains(session, server.Handshaken);
        Assert.Equal(session, FakeMicrosoftServer.Query(server.Api("getskuinformationbyproductedition").Single(), "sessionID"));
    }

    [Fact]
    public async Task Resolve_SendsTheRequestsTheWayThePageDoes()
    {
        var server = new FakeMicrosoftServer();
        var provider = Provider(server);
        var variant = await German(provider);
        server.Calls.Clear();

        await provider.ResolveAsync(variant, "x64", CancellationToken.None);

        var link = server.Api("GetProductDownloadLinksBySku").Single();
        Assert.Equal("606624d44113", FakeMicrosoftServer.Query(link, "profile"));
        Assert.Equal("27126", FakeMicrosoftServer.Query(link, "SKU"));
        Assert.Equal("undefined", FakeMicrosoftServer.Query(link, "ProductEditionId"));
        Assert.Equal("en-US", FakeMicrosoftServer.Query(link, "Locale"));

        var skus = server.Api("getskuinformationbyproductedition").Single();
        Assert.Equal("3813", FakeMicrosoftServer.Query(skus, "ProductEditionId"));
        Assert.Equal("undefined", FakeMicrosoftServer.Query(skus, "SKU"));

        Assert.All(server.Calls, c => Assert.StartsWith("Bootrix/", c.Headers.UserAgent.ToString(), StringComparison.Ordinal));
        Assert.All(server.Calls.Where(c => c.RequestUri!.AbsolutePath.Contains("software-download-connector", StringComparison.Ordinal)),
            c => Assert.Equal("https://www.microsoft.com/en-us/software-download/windows11", c.Headers.Referrer?.AbsoluteUri));
    }

    [Fact]
    public async Task Resolve_Handshake_PingsTheFrameAddressWithClientAndServerClock()
    {
        var server = new FakeMicrosoftServer();
        var provider = Provider(server);
        var variant = await German(provider);
        server.Calls.Clear();

        await provider.ResolveAsync(variant, "x64", CancellationToken.None);

        var script = server.Calls.Single(c => c.RequestUri!.AbsolutePath == "/mdt.js").RequestUri!;
        Assert.Equal("560dc9f3-1aa5-4a2f-b63c-9e18f8d0e175", FakeMicrosoftServer.Query(server.Calls.Single(c => c.RequestUri!.AbsolutePath == "/mdt.js"), "instanceId"));

        var ping = server.Calls.Single(c => c.RequestUri!.Host == "ov-df.microsoft.com" && c.RequestUri.AbsolutePath == "/");
        Assert.Equal(FakeMicrosoftServer.Query(ping, "session_id"), script.Query.Split("session_id=")[1]);
        Assert.Equal("8DF1F8E04F1BBA0", FakeMicrosoftServer.Query(ping, "w"));
        Assert.Equal("1790839893866", FakeMicrosoftServer.Query(ping, "rticks"));
        Assert.Equal(Now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), FakeMicrosoftServer.Query(ping, "mdt"));
        Assert.Equal("si", FakeMicrosoftServer.Query(ping, "PageId"));
    }

    [Fact]
    public async Task Resolve_Arm64_TakesTheLinkAndTheDigestOfTheArm64Page()
    {
        var server = new FakeMicrosoftServer();
        var provider = Provider(server);
        var variant = await German(provider);

        var request = await provider.ResolveAsync(variant, "arm64", CancellationToken.None);

        Assert.Contains("Windows11_Client_arm64_de-de_26300_9457.iso", request.Url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("bbf9f2284fe7559ea8bdf960851a80231eada3aa7e740958948b79ab01d23478", request.ExpectedHashes.Single().Hex);
        Assert.Equal("3816", FakeMicrosoftServer.Query(server.Api("getskuinformationbyproductedition").Last(), "ProductEditionId"));
    }

    [Fact]
    public async Task Resolve_WithoutArchitecture_TakesTheSixtyFourBitPc()
    {
        var provider = Provider(new FakeMicrosoftServer());
        var variant = await German(provider);

        var request = await provider.ResolveAsync(variant, null, CancellationToken.None);

        Assert.Contains("_x64_", request.Url.AbsoluteUri, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("x86", "Win10_22H2_German_x32v1.iso", "b0bfc1b9b176df0303ed3a91e7332cd1a8b57b07f25752cec9493e1333f88075")]
    [InlineData("x64", "Win10_22H2_German_x64v1.iso", "d1a41a09e9ae09631a087edf95d7f4eecab622f88b3c824d856cfea47fcc0b4c")]
    public async Task Resolve_Windows10_PicksTheLinkAndDigestOfTheWordSize(string architecture, string file, string sha256)
    {
        var provider = Provider(new FakeMicrosoftServer());
        var variant = await German(provider, "windows10");

        var request = await provider.ResolveAsync(variant, architecture, CancellationToken.None);

        Assert.Contains("/" + file + "?", request.Url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal(sha256, request.ExpectedHashes.Single().Hex);
    }

    [Fact]
    public async Task Resolve_LinkResolver_RepeatsTheExchangeInANewSessionAndReturnsAFreshAddress()
    {
        var server = new FakeMicrosoftServer();
        var provider = Provider(server);
        var variant = await German(provider);
        var request = await provider.ResolveAsync(variant, "x64", CancellationToken.None);
        var firstSession = FakeMicrosoftServer.Query(server.Api("GetProductDownloadLinksBySku").Single(), "sessionID");

        var fresh = await request.LinkResolver!(CancellationToken.None);

        Assert.Contains("t=00000002-", fresh.Query, StringComparison.Ordinal);
        Assert.StartsWith("https://software.download.prss.microsoft.com/dbazure/Windows11_Client_x64_de-de_26300_9457.iso", fresh.AbsoluteUri, StringComparison.Ordinal);
        var sessions = server.Api("GetProductDownloadLinksBySku").Select(c => FakeMicrosoftServer.Query(c, "sessionID")).ToList();
        Assert.Equal(2, sessions.Count);
        Assert.NotEqual(firstSession, sessions[1]);
        Assert.Equal(2, server.Handshaken.Count);

        // The downloader calls the resolver itself and checks what it returns before it uses it.
        var accepted = await DownloadHttp.ResolveLinkAsync(request, CancellationToken.None);
        Assert.Contains("t=00000003-", accepted.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_LinkResolver_DoesNotFetchThePageAgain()
    {
        var server = new FakeMicrosoftServer();
        var provider = Provider(server);
        var request = await provider.ResolveAsync(await German(provider), "x64", CancellationToken.None);
        server.Calls.Clear();

        await request.LinkResolver!(CancellationToken.None);

        Assert.DoesNotContain(server.Urls, u => u.Contains("/en-us/software-download/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Resolve_FirstAttemptRefused_RetriesOnceAndRegistersTheProfilingTag()
    {
        var server = new FakeMicrosoftServer { LinksNeedTag = true };
        var provider = Provider(server);
        var variant = await German(provider);
        server.Calls.Clear();

        var request = await provider.ResolveAsync(variant, "x64", CancellationToken.None);

        Assert.StartsWith("https://software.download.prss.microsoft.com/", request.Url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal(2, server.Api("GetProductDownloadLinksBySku").Count());
        var tag = Assert.Single(server.Calls, c => c.RequestUri!.Host == "vlscppe.microsoft.com");
        Assert.Equal("y6jn8c31", FakeMicrosoftServer.Query(tag, "org_id"));
        Assert.Equal(FakeMicrosoftServer.Query(tag, "session_id"), FakeMicrosoftServer.Query(server.Api("GetProductDownloadLinksBySku").Last(), "sessionID"));
    }

    [Fact]
    public async Task Resolve_Sentinel715_IsReportedAsBlockedWithTheCodeAndTheBrowserAddress()
    {
        var server = new FakeMicrosoftServer { RejectAllLinks = true };
        var provider = Provider(server);
        var variant = await German(provider);

        var error = await Assert.ThrowsAsync<MicrosoftDownloadBlockedException>(() => provider.ResolveAsync(variant, "x64", CancellationToken.None));

        Assert.Equal(ErrorCode.DownloadBlocked, error.Code);
        Assert.Equal("715-123130", error.MessageCode);
        Assert.Equal(["715-123130"], error.Arguments);
        Assert.Equal("https://www.microsoft.com/software-download/windows11", error.ManualUrl.AbsoluteUri);
        Assert.Contains("https://www.microsoft.com/software-download/windows11", error.Detail, StringComparison.Ordinal);
        Assert.Contains("browser", error.Detail, StringComparison.Ordinal);

        // Two attempts at most: the second one with the profiling tag. Nothing more is tried to get around the refusal.
        Assert.Equal(2, server.Api("GetProductDownloadLinksBySku").Count());
        Assert.Single(server.Calls, c => c.RequestUri!.Host == "vlscppe.microsoft.com");
    }

    [Fact]
    public async Task Resolve_BlockedMessage_HasLocalizedTextsInBothLanguages()
    {
        var provider = Provider(new FakeMicrosoftServer { RejectAllLinks = true });
        var variant = await German(provider);

        var error = await Assert.ThrowsAsync<MicrosoftDownloadBlockedException>(() => provider.ResolveAsync(variant, "x64", CancellationToken.None));

        var description = ErrorCatalog.Describe(error);
        Assert.Equal("BX5002", description.Code);
        Assert.Contains("715-123130", description.Cause, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_Http403OnTheLinkRequest_IsBlockedToo()
    {
        var server = new FakeMicrosoftServer { Override = r => r.RequestUri!.AbsolutePath.EndsWith("/GetProductDownloadLinksBySku", StringComparison.Ordinal) ? Status(HttpStatusCode.Forbidden) : null };
        var provider = Provider(server);
        var variant = await German(provider);

        var error = await Assert.ThrowsAsync<MicrosoftDownloadBlockedException>(() => provider.ResolveAsync(variant, "x64", CancellationToken.None));

        Assert.Equal("HTTP 403", error.MessageCode);
    }

    [Fact]
    public async Task Resolve_EmptyLinkList_IsCatalogUnavailable()
    {
        var server = new FakeMicrosoftServer
        {
            Override = r => r.RequestUri!.AbsolutePath.EndsWith("/GetProductDownloadLinksBySku", StringComparison.Ordinal)
                ? FakeMicrosoftServer.Json("""{"ProductDownloadOptions":[],"ProductDownload":null,"ValidationContainer":{"ErrorList":[],"Errors":[]}}""")
                : null,
        };
        var provider = Provider(server);
        var variant = await German(provider);

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ResolveAsync(variant, "x64", CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
        Assert.Contains("no download address", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_LanguageNoLongerOffered_IsCatalogUnavailable()
    {
        var provider = Provider(new FakeMicrosoftServer());
        var variant = await German(provider);
        var stale = new CatalogVariant
        {
            Id = variant.Id,
            ProductId = variant.ProductId,
            Provider = variant.Provider,
            Name = "Klingon",
            Architectures = ["x64"],
            Properties = new Dictionary<string, string> { ["route.x64"] = "windows11|3813", ["language"] = "Klingon" },
        };

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ResolveAsync(stale, "x64", CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
        Assert.Contains("'Klingon' is no longer offered", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_ServiceOffersOnlyAnotherArchitecture_NamesWhatItOffered()
    {
        var server = new FakeMicrosoftServer
        {
            Override = r => r.RequestUri!.AbsolutePath.EndsWith("/GetProductDownloadLinksBySku", StringComparison.Ordinal) && FakeMicrosoftServer.Query(r, "SKU") == "27126"
                ? FakeMicrosoftServer.Json(MicrosoftFixtures.Text("links-3816-de.json"))
                : null,
        };
        var provider = Provider(server);
        var variant = await German(provider);

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ResolveAsync(variant, "x64", CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
        Assert.Contains("offers no x64 download (offered: arm64)", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_HandshakeScriptChangedShape_IsCatalogUnavailable()
    {
        var server = new FakeMicrosoftServer
        {
            Override = r => r.RequestUri!.AbsolutePath == "/mdt.js" ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("console.log('moved');") } : null,
        };
        var provider = Provider(server);
        var variant = await German(provider);

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ResolveAsync(variant, "x64", CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
        Assert.Contains("no longer has the expected shape", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_PageWithoutHashTable_StillResolvesButWithoutExpectedHash()
    {
        var server = new FakeMicrosoftServer
        {
            Override = r => r.RequestUri!.AbsolutePath.EndsWith("/windows11", StringComparison.Ordinal)
                ? FakeMicrosoftServer.Html("""<select id="product-edition"><option value="3813">Windows 11</option></select>""")
                : null,
        };
        var provider = Provider(new FakeMicrosoftServer());
        var variant = await German(provider);

        var request = await Provider(server).ResolveAsync(variant, "x64", CancellationToken.None);

        Assert.Empty(request.ExpectedHashes);
        Assert.StartsWith("https://software.download.prss.microsoft.com/", request.Url.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_ArchitectureTheVariantDoesNotList_IsRejected()
    {
        var provider = Provider(new FakeMicrosoftServer());
        var variant = await German(provider);

        await Assert.ThrowsAsync<ArgumentException>(() => provider.ResolveAsync(variant, "x86", CancellationToken.None));
    }

    [Fact]
    public async Task Resolve_VariantOfAnotherProvider_IsRejected()
    {
        var foreign = new CatalogVariant { Id = "x", ProductId = "ubuntu", Provider = "ubuntu", Name = "Ubuntu", Architectures = ["x64"] };

        await Assert.ThrowsAsync<ArgumentException>(() => Provider(new FakeMicrosoftServer()).ResolveAsync(foreign, "x64", CancellationToken.None));
    }

    [Fact]
    public async Task CatalogService_ResolvesThroughTheProviderInterface()
    {
        var service = new CatalogService([Provider(new FakeMicrosoftServer())], NullLogger<CatalogService>.Instance);

        var listing = await service.ListProductsAsync(CancellationToken.None);
        var variants = await service.ListVariantsAsync(listing.Items.Single(p => p.Id == "windows10"), CancellationToken.None);
        var request = await service.ResolveAsync(variants.Single(v => v.Id == "de-DE"), "x86", CancellationToken.None);

        Assert.Empty(listing.Failures);
        Assert.Contains("Win10_22H2_German_x32v1.iso", request.Url.AbsoluteUri, StringComparison.Ordinal);
    }
}
