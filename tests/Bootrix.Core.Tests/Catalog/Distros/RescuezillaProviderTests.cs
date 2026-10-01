// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using Bootrix.Core.Catalog;
using Bootrix.Core.Catalog.Distros;
using Bootrix.Core.Errors;
using Bootrix.Core.Net;
using Bootrix.Core.Tests.Catalog.Distros.Support;
using Microsoft.Extensions.Time.Testing;

namespace Bootrix.Core.Tests.Catalog.Distros;

public class RescuezillaProviderTests
{
    private const string Page = "https://rescuezilla.com/download";
    private const string Releases = "https://github.com/rescuezilla/rescuezilla/releases/download/";

    private readonly FakeTimeProvider _time = new(DistroFixtures.CapturedOn);

    private static FakeWeb RescuezillaSite() => new FakeWeb()
        .ServeFixture(Page, "rescuezilla/download-page.html")
        .ServeFixture(Releases + "2.6.2/SHA256SUM", "rescuezilla/SHA256SUM-2.6.2");

    private RescuezillaProvider Provider(FakeWeb web) => new(web.CreateClient(), _time);

    [Fact]
    public void DownloadPage_NamesTheCurrentReleaseAndTheRecommendedIso()
    {
        var release = RescuezillaReleases.ParseDownloadPage(DistroFixtures.Text("rescuezilla/download-page.html"));

        Assert.Equal(new RescuezillaRelease("2.6.2", "rescuezilla-2.6.2-64bit.resolute.iso"), release);
    }

    [Theory]
    [InlineData("")]
    [InlineData("<html>Download soon</html>")]
    [InlineData("""<a href="https://github.com/rescuezilla/rescuezilla/releases/latest">latest</a>""")]
    public void DownloadPage_WithoutAReleaseLink_NamesNoRelease(string html)
    {
        Assert.Null(RescuezillaReleases.ParseDownloadPage(html));
    }

    [Fact]
    public void Images_AreTheIsosOfTheChecksumFile_NotTheDebianPackage()
    {
        var images = RescuezillaReleases.Images(ChecksumFile.Parse(DistroFixtures.Text("rescuezilla/SHA256SUM-2.6.2")));

        Assert.Equal(["noble", "oracular", "questing", "resolute"], images.Select(i => i.Base).Order());
        Assert.All(images, i => Assert.Equal("2.6.2", i.Version));
        Assert.All(images, i => Assert.Equal("x64", i.Architecture));
    }

    [Fact]
    public void Images_RecogniseThe32BitBuildsOfOlderReleases()
    {
        var images = RescuezillaReleases.Images(ChecksumFile.Parse(DistroFixtures.Text("rescuezilla/SHA256SUM-2.6.1")));

        var old = Assert.Single(images, i => i.Architecture == "i386");
        Assert.Equal("rescuezilla-2.6.1-32bit.bionic.iso", old.FileName);
    }

    [Fact]
    public async Task Variants_ListEveryBase_WithTheRecommendedOneFirst()
    {
        var variants = await Provider(RescuezillaSite()).ListVariantsAsync("rescuezilla", CancellationToken.None);

        Assert.Equal(
            ["2.6.2/x64-resolute", "2.6.2/x64-questing", "2.6.2/x64-oracular", "2.6.2/x64-noble"],
            variants.Select(v => v.Id));
        Assert.Equal("Rescuezilla 2.6.2 (Ubuntu resolute base)", variants[0].Name);
        Assert.Equal(["2.6.2/x64-resolute"], variants.Where(v => v.IsRecommended).Select(v => v.Id));
        Assert.All(variants, v => Assert.Equal(DistroProperties.TlsOnly, v.Properties[DistroProperties.HashTrust]));
        Assert.All(variants, v => Assert.Equal("2.6.2", v.Properties["tag"]));
    }

    [Fact]
    public async Task Resolve_UsesTheGitHubReleaseAssetAndItsDigest()
    {
        var provider = Provider(RescuezillaSite());
        var variant = (await provider.ListVariantsAsync("rescuezilla", CancellationToken.None))[0];

        var request = await provider.ResolveAsync(variant, null, CancellationToken.None);

        Assert.Equal(new Uri(Releases + "2.6.2/rescuezilla-2.6.2-64bit.resolute.iso"), request.Url);
        Assert.Equal(new FileHash(HashKind.Sha256, "20dfdad31d3da56b8dd3978159721f19071916f65e123003a850fdecec85ae3f"), Assert.Single(request.ExpectedHashes));
    }

    [Fact]
    public async Task Resolve_OfAnotherBase_UsesItsOwnLine()
    {
        var provider = Provider(RescuezillaSite());
        var variant = (await provider.ListVariantsAsync("rescuezilla", CancellationToken.None)).First(v => v.Id.EndsWith("noble", StringComparison.Ordinal));

        var request = await provider.ResolveAsync(variant, null, CancellationToken.None);

        Assert.Equal("285db0af83213e2297490ca1cfd74ecd607c3b0a2f1d14e11a8412c0b71eea50", request.ExpectedHashes[0].Hex);
    }

    [Fact]
    public async Task Variants_WhenThePageNamesNoRelease_ReportCatalogUnavailable()
    {
        var web = new FakeWeb().Serve(Page, "<html>new design</html>");

        var error = await Assert.ThrowsAsync<BootrixException>(() => Provider(web).ListVariantsAsync("rescuezilla", CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
    }

    [Fact]
    public async Task Variants_WhenTheChecksumAssetIsMissing_ReportCatalogUnavailable()
    {
        var web = RescuezillaSite().Fail(Releases + "2.6.2/SHA256SUM", HttpStatusCode.NotFound);

        var error = await Assert.ThrowsAsync<BootrixException>(() => Provider(web).ListVariantsAsync("rescuezilla", CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
    }

    [Fact]
    public async Task Product_IsARescueTool_AndAnotherIsACallerMistake()
    {
        var provider = Provider(RescuezillaSite());

        var product = Assert.Single(await provider.ListProductsAsync(CancellationToken.None));
        Assert.Equal(CatalogFamily.Rescue, product.Family);
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ListVariantsAsync("clonezilla", CancellationToken.None));
    }
}
