// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using Bootrix.Core.Catalog;
using Bootrix.Core.Catalog.Distros;
using Bootrix.Core.Errors;
using Bootrix.Core.Tests.Catalog.Distros.Support;
using Microsoft.Extensions.Time.Testing;

namespace Bootrix.Core.Tests.Catalog.Distros;

public class ZorinOsProviderTests
{
    private const string Page = "https://zorin.com/os/download/";

    private readonly FakeTimeProvider _time = new(DistroFixtures.CapturedOn);

    private ZorinOsProvider Provider(FakeWeb web) => new(web.CreateClient(), _time);

    private static FakeWeb ZorinSite() => new FakeWeb().ServeFixture(Page, "zorin/download-page.html");

    [Fact]
    public async Task Variants_AreTheFreeEditionsAndAllOfThemAreManualDownloads()
    {
        var variants = await Provider(ZorinSite()).ListVariantsAsync("zorin", CancellationToken.None);

        // The Pro edition is paid and has no download link on the page.
        Assert.Equal(["18/core", "18/education"], variants.Select(v => v.Id));
        Assert.Equal(["Zorin OS 18 Core", "Zorin OS 18 Education"], variants.Select(v => v.Name));
        Assert.Equal(["https://zorin.com/os/download/18/core/", "https://zorin.com/os/download/18/education/"], variants.Select(v => v.ManualUrl));
        Assert.All(variants, v => Assert.Equal("18", v.Version));
    }

    [Fact]
    public async Task OnlyTheNewestCoreEditionIsRecommended()
    {
        var variants = await Provider(ZorinSite()).ListVariantsAsync("zorin", CancellationToken.None);

        Assert.Equal(["18/core"], variants.Where(v => v.IsRecommended).Select(v => v.Id));
    }

    [Fact]
    public async Task ANewerVersionOnThePage_IsOfferedWithoutAnUpdate()
    {
        var web = new FakeWeb().Serve(Page, """<a href="/os/download/19/core/">x</a><a href="/os/download/18/core/">y</a><a href="/os/download/19/education/">z</a><a href="/os/download/19/core/">dup</a>""");

        var variants = await Provider(web).ListVariantsAsync("zorin", CancellationToken.None);

        Assert.Equal(["19/core", "19/education", "18/core"], variants.Select(v => v.Id));
        Assert.Equal(["19/core"], variants.Where(v => v.IsRecommended).Select(v => v.Id));
    }

    [Fact]
    public async Task Variants_HaveNoVerifiableDigest()
    {
        var variants = await Provider(ZorinSite()).ListVariantsAsync("zorin", CancellationToken.None);

        Assert.All(variants, v => Assert.Equal(DistroProperties.TlsOnly, v.Properties[DistroProperties.HashTrust]));
    }

    [Fact]
    public async Task Resolve_ExplainsWhereToDownloadInstead()
    {
        var provider = Provider(ZorinSite());
        var variant = (await provider.ListVariantsAsync("zorin", CancellationToken.None))[0];

        var error = await Assert.ThrowsAsync<NotSupportedException>(() => provider.ResolveAsync(variant, null, CancellationToken.None));

        Assert.Contains("https://zorin.com/os/download/18/core/", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Variants_WhenThePageHasNoEditionLinks_AreEmpty_AndWhenItIsDownTheCatalogIsUnavailable()
    {
        var redesigned = new FakeWeb().Serve(Page, "<html>new design</html>");
        Assert.Empty(await Provider(redesigned).ListVariantsAsync("zorin", CancellationToken.None));

        var down = new FakeWeb().Fail(Page, HttpStatusCode.ServiceUnavailable);
        var error = await Assert.ThrowsAsync<BootrixException>(() => Provider(down).ListVariantsAsync("zorin", CancellationToken.None));
        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
    }

    [Fact]
    public async Task Products_AreZorin_AndAnotherIsACallerMistake()
    {
        var provider = Provider(ZorinSite());

        Assert.Equal("zorin", Assert.Single(await provider.ListProductsAsync(CancellationToken.None)).Id);
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ListVariantsAsync("mint", CancellationToken.None));
    }
}
