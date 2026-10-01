// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using Bootrix.Core.Catalog;
using Bootrix.Core.Catalog.Distros;
using Bootrix.Core.Errors;
using Bootrix.Core.Net;
using Bootrix.Core.Tests.Catalog.Distros.Support;
using Microsoft.Extensions.Time.Testing;

namespace Bootrix.Core.Tests.Catalog.Distros;

public class GPartedLiveProviderTests
{
    private const string Site = "https://gparted.org/gparted-live/stable/";

    private readonly FakeTimeProvider _time = new(DistroFixtures.CapturedOn);

    private static FakeWeb GPartedSite() => new FakeWeb()
        .ServeFixture(Site + "CHECKSUMS.TXT", "gparted/CHECKSUMS.TXT")
        .ServeFixture(Site + "CHECKSUMS.TXT.gpg", "gparted/CHECKSUMS.TXT.gpg");

    private GPartedLiveProvider Provider(FakeWeb web) => new(web.CreateClient(), _time);

    [Fact]
    public async Task Variants_AreTheIsoListedOnceDespiteFourDigestSections()
    {
        var variants = await Provider(GPartedSite()).ListVariantsAsync("gparted-live", CancellationToken.None);

        var only = Assert.Single(variants);
        Assert.Equal("1.8.1-6/amd64", only.Id);
        Assert.Equal("GParted Live 1.8.1-6", only.Name);
        Assert.Equal("1.8.1-6", only.Version);
        Assert.Equal(["x64"], only.Architectures);
        Assert.True(only.IsRecommended);
        Assert.Equal(DistroProperties.PinnedKey, only.Properties[DistroProperties.HashTrust]);
    }

    [Fact]
    public async Task Variants_AreRescueTools_AndOlderArchitecturesAreRecognised()
    {
        var web = GPartedSite().Serve(
            Site + "CHECKSUMS.TXT",
            "### SHA256SUMS:\n"
            + new string('1', 64) + "  gparted-live-1.8.1-6-amd64.iso\n"
            + new string('2', 64) + "  gparted-live-1.5.0-1-i686.iso\n"
            + new string('3', 64) + "  gparted-live-1.5.0-1-i686-pae.iso\n");
        var provider = Provider(web);

        var product = Assert.Single(await provider.ListProductsAsync(CancellationToken.None));
        var variants = await provider.ListVariantsAsync("gparted-live", CancellationToken.None);

        Assert.Equal(CatalogFamily.Rescue, product.Family);
        // i686-pae has no architecture of its own in the catalog and is not offered.
        Assert.Equal(["1.8.1-6/amd64", "1.5.0-1/i686"], variants.Select(v => v.Id));
        Assert.Equal(["i386"], variants[1].Architectures);
    }

    [Fact]
    public async Task Resolve_VerifiesTheSignatureAndPrefersTheSha256Section()
    {
        var provider = Provider(GPartedSite());
        var variant = Assert.Single(await provider.ListVariantsAsync("gparted-live", CancellationToken.None));

        var request = await provider.ResolveAsync(variant, null, CancellationToken.None);

        Assert.Equal(
            new Uri("https://downloads.sourceforge.net/project/gparted/gparted-live-stable/1.8.1-6/gparted-live-1.8.1-6-amd64.iso"),
            Assert.Single(request.Sources).Url);
        Assert.Equal(new FileHash(HashKind.Sha256, "d789c38779f0d6f7026c12f44c2c52a04f66e28a1aea7d51f3045ad1bbf28411"), Assert.Single(request.ExpectedHashes));
    }

    [Fact]
    public async Task Resolve_RejectsAChecksumFileChangedAfterSigning()
    {
        var web = GPartedSite().Serve(Site + "CHECKSUMS.TXT", DistroFixtures.Text("gparted/CHECKSUMS.TXT").Replace("d789c387", "00000000", StringComparison.Ordinal));
        var provider = Provider(web);
        var variant = Assert.Single(await provider.ListVariantsAsync("gparted-live", CancellationToken.None));

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ResolveAsync(variant, null, CancellationToken.None));

        Assert.Equal(ErrorCode.SignatureInvalid, error.Code);
    }

    [Fact]
    public async Task Resolve_RejectsASignatureMadeByTheClonezillaKey()
    {
        var web = GPartedSite().ServeFixture(Site + "CHECKSUMS.TXT.gpg", "clonezilla/SHA256SUMS.gpg");
        var provider = Provider(web);
        var variant = Assert.Single(await provider.ListVariantsAsync("gparted-live", CancellationToken.None));

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ResolveAsync(variant, null, CancellationToken.None));

        Assert.Equal(ErrorCode.SignatureInvalid, error.Code);
    }

    [Fact]
    public async Task Variants_WhenTheSiteIsDown_ReportCatalogUnavailable()
    {
        var web = new FakeWeb().Fail(Site + "CHECKSUMS.TXT", HttpStatusCode.ServiceUnavailable);

        var error = await Assert.ThrowsAsync<BootrixException>(() => Provider(web).ListVariantsAsync("gparted-live", CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
    }

    [Fact]
    public async Task UnknownProduct_IsACallerMistake()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Provider(GPartedSite()).ListVariantsAsync("clonezilla", CancellationToken.None));
    }
}
