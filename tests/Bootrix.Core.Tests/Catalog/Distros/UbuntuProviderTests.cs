// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using Bootrix.Core.Catalog;
using Bootrix.Core.Catalog.Distros;
using Bootrix.Core.Errors;
using Bootrix.Core.Net;
using Bootrix.Core.Tests.Catalog.Distros.Support;
using Microsoft.Extensions.Time.Testing;

namespace Bootrix.Core.Tests.Catalog.Distros;

public class UbuntuProviderTests
{
    private const string MetaRelease = "https://changelogs.ubuntu.com/meta-release";
    private const string Releases = "https://releases.ubuntu.com/";

    private readonly FakeTimeProvider _time = new(DistroFixtures.CapturedOn);

    private static FakeWeb UbuntuSite() => new FakeWeb()
        .ServeFixture(MetaRelease, "ubuntu/meta-release")
        .ServeFixture(Releases + "26.04/SHA256SUMS", "ubuntu/ubuntu-26.04.SHA256SUMS")
        .ServeFixture(Releases + "26.04/SHA256SUMS.gpg", "ubuntu/ubuntu-26.04.SHA256SUMS.gpg")
        .ServeFixture(Releases + "24.04/SHA256SUMS", "ubuntu/ubuntu-24.04.SHA256SUMS")
        .ServeFixture(Releases + "24.04/SHA256SUMS.gpg", "ubuntu/ubuntu-24.04.SHA256SUMS.gpg")
        .ServeFixture(Releases + "22.04/SHA256SUMS", "ubuntu/ubuntu-22.04.SHA256SUMS")
        .ServeFixture(Releases + "22.04/SHA256SUMS.gpg", "ubuntu/ubuntu-22.04.SHA256SUMS.gpg");

    private UbuntuProvider Provider(FakeWeb web) => new(web.CreateClient(), _time);

    [Fact]
    public async Task Products_AreUbuntuAndItsFlavours()
    {
        var products = await Provider(UbuntuSite()).ListProductsAsync(CancellationToken.None);

        Assert.Equal(["ubuntu", "kubuntu", "xubuntu", "lubuntu", "ubuntu-mate"], products.Select(p => p.Id));
        Assert.All(products, p =>
        {
            Assert.Equal("ubuntu", p.Provider);
            Assert.Equal(CatalogFamily.Linux, p.Family);
        });
    }

    [Fact]
    public async Task Variants_OfferNewestPointReleaseOfEverySeriesInStandardSupport()
    {
        var variants = await Provider(UbuntuSite()).ListVariantsAsync("ubuntu", CancellationToken.None);

        // 20.04 is still flagged as supported by the meta-release list but is past its five years.
        Assert.Equal(
            ["26.04.1/desktop", "26.04.1/server", "24.04.5.1/desktop", "24.04.5/server", "22.04.5/desktop", "22.04.5/server"],
            variants.Select(v => v.Id));

        var desktop = variants[0];
        Assert.Equal("Ubuntu 26.04.1 LTS Desktop", desktop.Name);
        Assert.Equal("26.04.1", desktop.Version);
        Assert.Equal(new DateOnly(2026, 4, 23), desktop.ReleaseDate);
        Assert.Equal([Architectures()], desktop.Architectures);
        Assert.Equal("ubuntu", desktop.ProductId);
        Assert.Equal("ubuntu", desktop.Provider);
    }

    [Fact]
    public async Task Variants_MarkTheNewestLtsDesktopAsRecommended()
    {
        var variants = await Provider(UbuntuSite()).ListVariantsAsync("ubuntu", CancellationToken.None);

        Assert.Equal(["26.04.1/desktop"], variants.Where(v => v.IsRecommended).Select(v => v.Id));
    }

    [Fact]
    public async Task Variants_ClaimASignedDigest()
    {
        var variants = await Provider(UbuntuSite()).ListVariantsAsync("ubuntu", CancellationToken.None);

        Assert.All(variants, v => Assert.Equal(DistroProperties.PinnedKey, v.Properties[DistroProperties.HashTrust]));
    }

    [Fact]
    public async Task Variants_OfAFlavourComeFromTheCdimageServerAndSkipSeriesItDidNotRelease()
    {
        var web = new FakeWeb()
            .ServeFixture(MetaRelease, "ubuntu/meta-release")
            .ServeFixture("https://cdimage.ubuntu.com/ubuntu-mate/releases/24.04/release/SHA256SUMS", "ubuntu/ubuntu-mate-24.04.SHA256SUMS");

        var variants = await Provider(web).ListVariantsAsync("ubuntu-mate", CancellationToken.None);

        var only = Assert.Single(variants);
        Assert.Equal("24.04.5/desktop", only.Id);
        Assert.Equal("Ubuntu MATE 24.04.5 LTS Desktop", only.Name);
        Assert.Equal("ubuntu-mate-24.04.5-desktop-amd64.iso", only.Properties["file"]);
        Assert.False(only.IsRecommended);
    }

    [Fact]
    public async Task Variants_AreCachedForTenMinutes()
    {
        var web = UbuntuSite();
        var provider = Provider(web);

        await provider.ListVariantsAsync("ubuntu", CancellationToken.None);
        await provider.ListVariantsAsync("ubuntu", CancellationToken.None);
        Assert.Equal(1, web.Count(MetaRelease));

        _time.Advance(TimeSpan.FromMinutes(11));
        await provider.ListVariantsAsync("ubuntu", CancellationToken.None);
        Assert.Equal(2, web.Count(MetaRelease));
    }

    [Fact]
    public async Task Variants_OfAnUnknownProductAreACallerMistake()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Provider(UbuntuSite()).ListVariantsAsync("debian", CancellationToken.None));
    }

    [Fact]
    public async Task Variants_WhenTheMetaReleaseListIsDown_ReportCatalogUnavailable()
    {
        var web = new FakeWeb().Fail(MetaRelease, HttpStatusCode.ServiceUnavailable);

        var error = await Assert.ThrowsAsync<BootrixException>(() => Provider(web).ListVariantsAsync("ubuntu", CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
    }

    [Fact]
    public async Task Resolve_UsesTheDigestFromTheSignedChecksumFile()
    {
        var web = UbuntuSite();
        var provider = Provider(web);
        var variant = (await provider.ListVariantsAsync("ubuntu", CancellationToken.None)).First(v => v.Id == "26.04.1/desktop");

        var request = await provider.ResolveAsync(variant, null, CancellationToken.None);

        Assert.Equal(new Uri("https://releases.ubuntu.com/26.04/ubuntu-26.04.1-desktop-amd64.iso"), request.Url);
        var hash = Assert.Single(request.ExpectedHashes);
        Assert.Equal(HashKind.Sha256, hash.Kind);
        Assert.Equal("601e30fbf5d97759367c632e2c33630665039b7e2158fd068403da3ccf1bda1f", hash.Hex);
    }

    [Fact]
    public async Task Resolve_AddsMirrorsWithTheSameFile()
    {
        var web = UbuntuSite();
        var provider = Provider(web);
        var variant = (await provider.ListVariantsAsync("ubuntu", CancellationToken.None)).First(v => v.Id == "24.04.5/server");

        var request = await provider.ResolveAsync(variant, "x64", CancellationToken.None);

        Assert.Equal(new Uri("https://releases.ubuntu.com/24.04/ubuntu-24.04.5-live-server-amd64.iso"), request.Sources[0].Url);
        Assert.Equal(1, request.Sources[0].Priority);
        Assert.Contains(request.Sources, s => s.Url == new Uri("https://ftp.fau.de/ubuntu-releases/24.04/ubuntu-24.04.5-live-server-amd64.iso") && s.Location == "DE" && s.Priority == 2);
        Assert.Equal("97f3d7ffb032c3eb3b23d2c8be9cc76e60c2c1f2c0146ba5ba9fe01cafae0fd8", request.ExpectedHashes[0].Hex);
    }

    [Fact]
    public async Task Resolve_FlavourHasNoMirrorsBesidesTheCdimageServer()
    {
        var web = new FakeWeb()
            .ServeFixture(MetaRelease, "ubuntu/meta-release")
            .ServeFixture("https://cdimage.ubuntu.com/kubuntu/releases/26.04/release/SHA256SUMS", "ubuntu/kubuntu-26.04.SHA256SUMS")
            .ServeFixture("https://cdimage.ubuntu.com/kubuntu/releases/26.04/release/SHA256SUMS.gpg", "ubuntu/kubuntu-26.04.SHA256SUMS.gpg");
        var provider = Provider(web);
        var variant = Assert.Single(await provider.ListVariantsAsync("kubuntu", CancellationToken.None));

        var request = await provider.ResolveAsync(variant, null, CancellationToken.None);

        Assert.Equal(new Uri("https://cdimage.ubuntu.com/kubuntu/releases/26.04/release/kubuntu-26.04.1-desktop-amd64.iso"), Assert.Single(request.Sources).Url);
        Assert.Equal("831e4d4bb85098339ba43d3502cd6619b27e76daf37246a084cd68a6413090b8", request.ExpectedHashes[0].Hex);
    }

    [Fact]
    public async Task Resolve_RejectsAChecksumFileThatWasChangedAfterSigning()
    {
        var web = UbuntuSite();
        var provider = Provider(web);
        var variant = (await provider.ListVariantsAsync("ubuntu", CancellationToken.None)).First(v => v.Id == "26.04.1/desktop");
        var forged = DistroFixtures.Text("ubuntu/ubuntu-26.04.SHA256SUMS").Replace("601e30fb", "deadbeef", StringComparison.Ordinal);
        web.Serve(Releases + "26.04/SHA256SUMS", forged);

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ResolveAsync(variant, null, CancellationToken.None));

        Assert.Equal(ErrorCode.SignatureInvalid, error.Code);
    }

    [Fact]
    public async Task Resolve_RejectsASignatureFromAKeyThatIsNotPinned()
    {
        var web = UbuntuSite();
        var provider = Provider(web);
        var variant = (await provider.ListVariantsAsync("ubuntu", CancellationToken.None)).First(v => v.Id == "26.04.1/desktop");
        // Debian's signature over Debian's file: a perfectly valid signature, but not by Ubuntu's key.
        web.ServeFixture(Releases + "26.04/SHA256SUMS", "debian/netinst.SHA256SUMS");
        web.ServeFixture(Releases + "26.04/SHA256SUMS.gpg", "debian/netinst.SHA256SUMS.sign");

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ResolveAsync(variant, null, CancellationToken.None));

        Assert.Equal(ErrorCode.SignatureInvalid, error.Code);
    }

    [Fact]
    public async Task Resolve_WithoutASignatureFile_FailsInsteadOfFallingBackToAnUnsignedDigest()
    {
        var web = UbuntuSite();
        var provider = Provider(web);
        var variant = (await provider.ListVariantsAsync("ubuntu", CancellationToken.None)).First(v => v.Id == "26.04.1/desktop");
        web.Fail(Releases + "26.04/SHA256SUMS.gpg", HttpStatusCode.NotFound);

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ResolveAsync(variant, null, CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
    }

    [Fact]
    public async Task Resolve_WhenTheSignedFileNoLongerListsTheImage_ReportsTheMissingEntry()
    {
        var web = UbuntuSite();
        var provider = Provider(web);
        var variant = (await provider.ListVariantsAsync("ubuntu", CancellationToken.None)).First(v => v.Id == "22.04.5/desktop");
        // The signed 24.04 file does not know the 22.04 desktop image.
        web.ServeFixture(Releases + "22.04/SHA256SUMS", "ubuntu/ubuntu-24.04.SHA256SUMS");
        web.ServeFixture(Releases + "22.04/SHA256SUMS.gpg", "ubuntu/ubuntu-24.04.SHA256SUMS.gpg");

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ResolveAsync(variant, null, CancellationToken.None));

        Assert.Equal(ErrorCode.ChecksumFileInvalid, error.Code);
    }

    [Fact]
    public async Task Resolve_RejectsAnArchitectureTheVariantDoesNotOffer()
    {
        var web = UbuntuSite();
        var provider = Provider(web);
        var variant = (await provider.ListVariantsAsync("ubuntu", CancellationToken.None))[0];

        await Assert.ThrowsAsync<ArgumentException>(() => provider.ResolveAsync(variant, "arm64", CancellationToken.None));
    }

    [Fact]
    public async Task Resolve_RejectsAVariantFromAnotherProvider()
    {
        var foreign = new CatalogVariant { Id = "x", ProductId = "x", Provider = "other", Name = "x" };

        await Assert.ThrowsAsync<ArgumentException>(() => Provider(UbuntuSite()).ResolveAsync(foreign, null, CancellationToken.None));
    }

    private static string Architectures() => "x64";
}
