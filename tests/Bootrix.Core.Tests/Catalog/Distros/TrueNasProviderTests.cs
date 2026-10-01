// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using Bootrix.Core.Catalog;
using Bootrix.Core.Catalog.Distros;
using Bootrix.Core.Errors;
using Bootrix.Core.Net;
using Bootrix.Core.Tests.Catalog.Distros.Support;
using Microsoft.Extensions.Time.Testing;

namespace Bootrix.Core.Tests.Catalog.Distros;

public class TrueNasProviderTests
{
    private const string Update = "https://update.sys.truenas.net/scale/";
    private const string Download = "https://download.sys.truenas.net/";

    private readonly FakeTimeProvider _time = new(DistroFixtures.CapturedOn);

    private static FakeWeb TrueNasSite() => new FakeWeb()
        .ServeFixture(Update + "trains.json", "truenas/trains.json")
        .ServeFixture(Update + "TrueNAS-SCALE-Goldeye/manifest.json", "truenas/Goldeye.manifest.json")
        .ServeFixture(Update + "TrueNAS-SCALE-Fangtooth/manifest.json", "truenas/Fangtooth.manifest.json")
        .ServeFixture(Update + "TrueNAS-SCALE-ElectricEel/manifest.json", "truenas/ElectricEel.manifest.json")
        .ServeFixture(Update + "TrueNAS-SCALE-Dragonfish/manifest.json", "truenas/Dragonfish.manifest.json")
        .ServeFixture(Download + "TrueNAS-SCALE-Goldeye/25.10.7/TrueNAS-SCALE-25.10.7.iso.sha256", "truenas/25.10.7.iso.sha256")
        .ServeFixture(Download + "TrueNAS-SCALE-ElectricEel/24.10.2.4/TrueNAS-SCALE-24.10.2.4.iso.sha256", "truenas/24.10.2.4.iso.sha256");

    private TrueNasProvider Provider(FakeWeb web) => new(web.CreateClient(), _time);

    [Fact]
    public void Trains_SkipBetaReleaseCandidateAndEndOfLifeTrains()
    {
        var trains = TrueNasReleases.Trains(DistroFixtures.Text("truenas/trains.json"));

        Assert.Equal(
            ["TrueNAS-SCALE-Dragonfish", "TrueNAS-SCALE-ElectricEel", "TrueNAS-SCALE-Fangtooth", "TrueNAS-SCALE-Goldeye"],
            trains.Select(t => t.Name));
        Assert.Equal(["Dragonfish", "ElectricEel", "Fangtooth", "Goldeye"], trains.Select(t => t.Codename));
    }

    [Fact]
    public void Manifest_NamesTheVersionAndDate()
    {
        var release = TrueNasReleases.Manifest(DistroFixtures.Text("truenas/Goldeye.manifest.json"));

        Assert.Equal(new TrueNasRelease("25.10.7", new DateOnly(2026, 8, 31)), release);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"version":"not-a-version"}""")]
    [InlineData("""{"version":""}""")]
    public void Manifest_WithoutAVersion_IsNotARelease(string json)
    {
        Assert.Null(TrueNasReleases.Manifest(json));
    }

    [Fact]
    public async Task Variants_AreTheCurrentTrainsNewestFirst_AndNeverAskForBetaManifests()
    {
        var web = TrueNasSite();

        var variants = await Provider(web).ListVariantsAsync("truenas", CancellationToken.None);

        Assert.Equal(["25.10.7", "25.04.2.6", "24.10.2.4", "24.04.2.5"], variants.Select(v => v.Id));
        Assert.Equal(
            ["TrueNAS 25.10.7 (Goldeye)", "TrueNAS 25.04.2.6 (Fangtooth)", "TrueNAS 24.10.2.4 (Electric Eel)", "TrueNAS 24.04.2.5 (Dragonfish)"],
            variants.Select(v => v.Name));
        Assert.Equal(new DateOnly(2026, 8, 31), variants[0].ReleaseDate);
        Assert.Equal(["25.10.7"], variants.Where(v => v.IsRecommended).Select(v => v.Id));
        Assert.DoesNotContain(web.Requests, r => r.AbsoluteUri.Contains("BETA", StringComparison.Ordinal) || r.AbsoluteUri.Contains("-RC", StringComparison.Ordinal));
        Assert.All(variants, v => Assert.Equal(DistroProperties.TlsOnly, v.Properties[DistroProperties.HashTrust]));
    }

    [Fact]
    public async Task Variants_SkipATrainWhoseManifestIsMissing()
    {
        var web = TrueNasSite().Fail(Update + "TrueNAS-SCALE-Dragonfish/manifest.json", HttpStatusCode.NotFound);

        var variants = await Provider(web).ListVariantsAsync("truenas", CancellationToken.None);

        Assert.DoesNotContain(variants, v => v.Id == "24.04.2.5");
        Assert.Equal(3, variants.Count);
    }

    [Fact]
    public async Task Resolve_ReadsTheBareDigestAndOffersBothDownloadHosts()
    {
        var provider = Provider(TrueNasSite());
        var variant = (await provider.ListVariantsAsync("truenas", CancellationToken.None))[0];

        var request = await provider.ResolveAsync(variant, null, CancellationToken.None);

        Assert.Equal(
            [
                new Uri("https://download.sys.truenas.net/TrueNAS-SCALE-Goldeye/25.10.7/TrueNAS-SCALE-25.10.7.iso"),
                new Uri("https://download.truenas.com/TrueNAS-SCALE-Goldeye/25.10.7/TrueNAS-SCALE-25.10.7.iso"),
            ],
            request.Sources.Select(s => s.Url));
        Assert.Equal(new FileHash(HashKind.Sha256, "54ce9441ce66966a392e28f63604ca3c2c083d0bec4db7bb5af2f74f7a007c8e"), Assert.Single(request.ExpectedHashes));
    }

    [Fact]
    public async Task Resolve_ReadsTheNamedFormatOlderReleasesUse()
    {
        var provider = Provider(TrueNasSite());
        var variant = (await provider.ListVariantsAsync("truenas", CancellationToken.None)).First(v => v.Id == "24.10.2.4");

        var request = await provider.ResolveAsync(variant, null, CancellationToken.None);

        Assert.Equal("9aa926f3fe72e012bd01d6870e9c2e4475a43be0d38ad61b31a607ce29c38a31", request.ExpectedHashes[0].Hex);
    }

    [Fact]
    public async Task Resolve_WhenTheChecksumIsMissing_ReportsCatalogUnavailable()
    {
        var provider = Provider(TrueNasSite());
        var variant = (await provider.ListVariantsAsync("truenas", CancellationToken.None)).First(v => v.Id == "25.04.2.6");

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ResolveAsync(variant, null, CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
    }

    [Fact]
    public async Task Variants_WhenTheTrainListIsDown_ReportCatalogUnavailable()
    {
        var web = new FakeWeb().Fail(Update + "trains.json", HttpStatusCode.InternalServerError);

        var error = await Assert.ThrowsAsync<BootrixException>(() => Provider(web).ListVariantsAsync("truenas", CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
    }

    [Theory]
    [InlineData("ElectricEel", "Electric Eel")]
    [InlineData("Goldeye", "Goldeye")]
    [InlineData("Dragonfish", "Dragonfish")]
    public void Codenames_AreBrokenIntoWords(string codename, string expected)
    {
        Assert.Equal(expected, TrueNasReleases.Spaced(codename));
    }

    [Fact]
    public async Task Products_AreTrueNas_AndAnotherIsACallerMistake()
    {
        var provider = Provider(TrueNasSite());

        Assert.Equal("truenas", Assert.Single(await provider.ListProductsAsync(CancellationToken.None)).Id);
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ListVariantsAsync("freenas", CancellationToken.None));
    }
}
