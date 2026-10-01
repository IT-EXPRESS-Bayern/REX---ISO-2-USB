// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
using Bootrix.Core.Catalog;
using Bootrix.Core.Catalog.Microsoft;
using Bootrix.Core.Errors;

using static Bootrix.Core.Tests.Catalog.Microsoft.IsoProviderSupport;

namespace Bootrix.Core.Tests.Catalog.Microsoft;

public class MicrosoftIsoProviderListingTests
{
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
        Assert.Equal("windows11|3813|German", german.Properties["route.x64"]);
        Assert.Equal("windows11arm64|3816|German", german.Properties["route.arm64"]);
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
        Assert.Equal("windows10ISO|2618|German", german.Properties["route.x64"]);
        Assert.Equal("windows10ISO|2618|German", german.Properties["route.x86"]);

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
}
