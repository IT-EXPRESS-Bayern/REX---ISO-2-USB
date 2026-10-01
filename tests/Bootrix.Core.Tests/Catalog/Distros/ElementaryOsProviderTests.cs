// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using Bootrix.Core.Catalog;
using Bootrix.Core.Catalog.Distros;
using Bootrix.Core.Errors;
using Bootrix.Core.Net;
using Bootrix.Core.Tests.Catalog.Distros.Support;
using Microsoft.Extensions.Time.Testing;

namespace Bootrix.Core.Tests.Catalog.Distros;

public class ElementaryOsProviderTests
{
    private const string Front = "https://elementary.io/";
    private const string Amd64 = "https://dl.elementaryos.org/MTc5MDgzODYxOA==/elementaryos-8.1-stable-amd64.20260219.iso";
    private const string Arm64 = "https://dl.elementaryos.org/MTc5MDgzODYxOA==/elementaryos-8.1-stable-arm64.20260219.iso";

    private readonly FakeTimeProvider _time = new(DistroFixtures.CapturedOn);

    private static FakeWeb ElementarySite() => new FakeWeb()
        .ServeFixture(Front, "elementary/front-page-download-dialog.html")
        .ServeFixture(Amd64 + ".sha256.txt", "elementary/amd64.sha256.txt")
        .ServeFixture(Arm64 + ".sha256.txt", "elementary/arm64.sha256.txt");

    private ElementaryOsProvider Provider(FakeWeb web) => new(web.CreateClient(), _time);

    [Fact]
    public void FrontPage_LinksOneImagePerArchitectureWithTheTimeLimitedAddress()
    {
        var downloads = ElementaryDownloads.Parse(DistroFixtures.Text("elementary/front-page-download-dialog.html"));

        // The magnet links on the same page carry the address percent-encoded and are not mistaken for downloads.
        Assert.Equal(2, downloads.Count);
        var amd64 = Assert.Single(downloads, d => d.Architecture == "x64");
        Assert.Equal("8.1", amd64.Version);
        Assert.Equal("amd64", amd64.VendorArchitecture);
        Assert.Equal("elementaryos-8.1-stable-amd64.20260219.iso", amd64.FileName);
        Assert.Equal(new DateOnly(2026, 2, 19), amd64.Build);
        Assert.Equal(new Uri(Amd64), amd64.Url);
        Assert.Equal(new Uri(Arm64), Assert.Single(downloads, d => d.Architecture == "arm64").Url);
    }

    [Fact]
    public async Task Variants_AreTheCurrentReleaseForPcsAndArm()
    {
        var variants = await Provider(ElementarySite()).ListVariantsAsync("elementary", CancellationToken.None);

        var only = Assert.Single(variants);
        Assert.Equal("8.1", only.Id);
        Assert.Equal("elementary OS 8.1", only.Name);
        Assert.Equal(["x64", "arm64"], only.Architectures);
        Assert.Equal(new DateOnly(2026, 2, 19), only.ReleaseDate);
        Assert.True(only.IsRecommended);
        Assert.Equal(DistroProperties.TlsOnly, only.Properties[DistroProperties.HashTrust]);
    }

    [Fact]
    public async Task Resolve_ReadsTheDigestBesideTheFileAndKeepsTheAddress()
    {
        var provider = Provider(ElementarySite());
        var variant = Assert.Single(await provider.ListVariantsAsync("elementary", CancellationToken.None));

        var x64 = await provider.ResolveAsync(variant, "x64", CancellationToken.None);
        var arm = await provider.ResolveAsync(variant, "arm64", CancellationToken.None);

        Assert.Equal(new Uri(Amd64), x64.Url);
        Assert.Equal(new FileHash(HashKind.Sha256, "bda93040d08c05911fb159f8150bf8f4ef2db6567ef6e2acd197cb6f395d3446"), Assert.Single(x64.ExpectedHashes));
        Assert.Equal("85116d48c406ae7cd60c936050a099d4b8610321273f6f0a694796db4d4e86ba", arm.ExpectedHashes[0].Hex);
    }

    [Fact]
    public async Task LinkResolver_ReadsTheFrontPageAgainForANewCode()
    {
        var web = ElementarySite();
        var provider = Provider(web);
        var variant = Assert.Single(await provider.ListVariantsAsync("elementary", CancellationToken.None));
        var request = await provider.ResolveAsync(variant, "x64", CancellationToken.None);
        web.Serve(Front, DistroFixtures.Text("elementary/front-page-download-dialog.html").Replace("MTc5MDgzODYxOA==", "MTc5MTAwMDAwMA==", StringComparison.Ordinal));
        var before = web.Count(Front);

        var renewed = await request.LinkResolver!(CancellationToken.None);

        Assert.Equal(new Uri("https://dl.elementaryos.org/MTc5MTAwMDAwMA==/elementaryos-8.1-stable-amd64.20260219.iso"), renewed);
        Assert.Equal(before + 1, web.Count(Front));
    }

    [Fact]
    public async Task Resolve_WhenTheReleaseIsNoLongerLinked_ReportsCatalogUnavailable()
    {
        var web = ElementarySite();
        var provider = Provider(web);
        var variant = Assert.Single(await provider.ListVariantsAsync("elementary", CancellationToken.None));
        web.Serve(Front, "<html>nothing here</html>");
        _time.Advance(TimeSpan.FromMinutes(11));

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ResolveAsync(variant, "x64", CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
    }

    [Fact]
    public async Task Resolve_WhenTheCodeHasExpired_ReportsCatalogUnavailable()
    {
        var web = ElementarySite().Fail(Amd64 + ".sha256.txt", HttpStatusCode.BadRequest);
        var provider = Provider(web);
        var variant = Assert.Single(await provider.ListVariantsAsync("elementary", CancellationToken.None));

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ResolveAsync(variant, "x64", CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
    }

    [Fact]
    public async Task Variants_WhenThePageHasNoDownloads_AreEmpty()
    {
        var web = new FakeWeb().Serve(Front, "<html>JavaScript is required</html>");

        Assert.Empty(await Provider(web).ListVariantsAsync("elementary", CancellationToken.None));
    }

    [Fact]
    public async Task Products_AreElementary_AndAnotherIsACallerMistake()
    {
        var provider = Provider(ElementarySite());

        Assert.Equal("elementary", Assert.Single(await provider.ListProductsAsync(CancellationToken.None)).Id);
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ListVariantsAsync("zorin", CancellationToken.None));
    }
}
