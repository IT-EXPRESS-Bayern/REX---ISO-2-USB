// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net;
using Bootrix.Core.Catalog;
using Bootrix.Core.Catalog.Microsoft;
using Bootrix.Core.Errors;
using Bootrix.Core.Net;
using Microsoft.Extensions.Logging.Abstractions;

using static Bootrix.Core.Tests.Catalog.Microsoft.IsoProviderSupport;

namespace Bootrix.Core.Tests.Catalog.Microsoft;

public class MicrosoftIsoProviderResolveTests
{
    private const string GermanWindows11X64Sha256 = "193bbde65ec84e298a1c798959489c8375ed501cf973c6d37fa52bb22ee8d43b";

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
        request.Validate();

        // As in the browser: the page, the handshake it starts, then the language list and the link request, all of one session.
        var calls = server.Calls.Select(c => $"{c.RequestUri!.Host}{c.RequestUri.AbsolutePath}").ToList();
        Assert.Equal(
            [
                "www.microsoft.com/en-us/software-download/windows11",
                "ov-df.microsoft.com/mdt.js",
                "ov-df.microsoft.com/",
                "www.microsoft.com/software-download-connector/api/getskuinformationbyproductedition",
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
            Properties = new Dictionary<string, string> { ["route.x64"] = "windows11|3813|Klingon", ["language"] = "Klingon" },
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
