// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using Bootrix.Core.Catalog;
using Bootrix.Core.Catalog.Distros;
using Bootrix.Core.Errors;
using Bootrix.Core.Net;
using Bootrix.Core.Tests.Catalog.Distros.Support;
using Microsoft.Extensions.Time.Testing;

namespace Bootrix.Core.Tests.Catalog.Distros;

public class DebianProviderTests
{
    private const string Netinst = "https://cdimage.debian.org/debian-cd/current/amd64/iso-cd/";
    private const string NetinstArm = "https://cdimage.debian.org/debian-cd/current/arm64/iso-cd/";
    private const string Live = "https://cdimage.debian.org/debian-cd/current-live/amd64/iso-hybrid/";

    private readonly FakeTimeProvider _time = new(DistroFixtures.CapturedOn);

    private static FakeWeb DebianSite() => new FakeWeb()
        .ServeFixture(Netinst + "SHA256SUMS", "debian/netinst.SHA256SUMS")
        .ServeFixture(Netinst + "SHA256SUMS.sign", "debian/netinst.SHA256SUMS.sign")
        .ServeFixture(NetinstArm + "SHA256SUMS", "debian/netinst-arm64.SHA256SUMS")
        .ServeFixture(NetinstArm + "SHA256SUMS.sign", "debian/netinst-arm64.SHA256SUMS.sign")
        .ServeFixture(Live + "SHA256SUMS", "debian/live.SHA256SUMS")
        .ServeFixture(Live + "SHA256SUMS.sign", "debian/live.SHA256SUMS.sign");

    private DebianProvider Provider(FakeWeb web) => new(web.CreateClient(), _time);

    [Fact]
    public async Task Variants_AreTheNetinstInstallerAndTheLiveDesktops()
    {
        var variants = await Provider(DebianSite()).ListVariantsAsync("debian", CancellationToken.None);

        Assert.Equal(
            [
                "13.7.0/netinst",
                "13.7.0/live-cinnamon",
                "13.7.0/live-debian-junior",
                "13.7.0/live-gnome",
                "13.7.0/live-kde",
                "13.7.0/live-lxde",
                "13.7.0/live-lxqt",
                "13.7.0/live-mate",
                "13.7.0/live-standard",
                "13.7.0/live-xfce",
            ],
            variants.Select(v => v.Id));
        Assert.All(variants, v => Assert.Equal("13.7.0", v.Version));
    }

    [Fact]
    public async Task Netinst_IsRecommendedAndComesForPcsAndArmServers()
    {
        var variants = await Provider(DebianSite()).ListVariantsAsync("debian", CancellationToken.None);

        var netinst = Assert.Single(variants, v => v.IsRecommended);
        Assert.Equal("Debian 13.7.0 netinst", netinst.Name);
        Assert.Equal(["x64", "arm64"], netinst.Architectures);
        Assert.Equal(["x64"], variants.First(v => v.Id.EndsWith("gnome", StringComparison.Ordinal)).Architectures);
    }

    [Fact]
    public async Task LiveVariants_NameTheDesktop()
    {
        var variants = await Provider(DebianSite()).ListVariantsAsync("debian", CancellationToken.None);

        Assert.Equal("Debian 13.7.0 live (KDE Plasma)", variants.First(v => v.Id == "13.7.0/live-kde").Name);
        Assert.Equal("Debian 13.7.0 live (without desktop)", variants.First(v => v.Id == "13.7.0/live-standard").Name);
        Assert.Equal("Debian 13.7.0 live (Debian Junior)", variants.First(v => v.Id == "13.7.0/live-debian-junior").Name);
    }

    [Fact]
    public async Task Resolve_Netinst_UsesTheSignedDigestAndListsMirrors()
    {
        var provider = Provider(DebianSite());
        var variant = (await provider.ListVariantsAsync("debian", CancellationToken.None)).First(v => v.IsRecommended);

        var request = await provider.ResolveAsync(variant, "x64", CancellationToken.None);

        Assert.Equal(new Uri(Netinst + "debian-13.7.0-amd64-netinst.iso"), request.Url);
        Assert.Equal(new FileHash(HashKind.Sha256, "a7ef94ac2fb9a7fec454552abd629b7cc9d5155c886165a45649f5ce6167e355"), Assert.Single(request.ExpectedHashes));
        Assert.Contains(request.Sources, s => s.Url == new Uri("https://ftp.fau.de/debian-cd/current/amd64/iso-cd/debian-13.7.0-amd64-netinst.iso") && s.Location == "DE");
        Assert.Contains(request.Sources, s => s.Url.Host == "mirror.dogado.de");
    }

    [Fact]
    public async Task Resolve_Netinst_ForArm64_ReadsTheArm64Directory()
    {
        var provider = Provider(DebianSite());
        var variant = (await provider.ListVariantsAsync("debian", CancellationToken.None)).First(v => v.IsRecommended);

        var request = await provider.ResolveAsync(variant, "arm64", CancellationToken.None);

        Assert.Equal(new Uri(NetinstArm + "debian-13.7.0-arm64-netinst.iso"), request.Url);
        Assert.Equal("6e93fa1759bd9d4b0fc11e938987de6967ee7de5297dac1be27c3a75cc17024b", request.ExpectedHashes[0].Hex);
        Assert.Contains(request.Sources, s => s.Url.AbsolutePath == "/debian-cd/current/arm64/iso-cd/debian-13.7.0-arm64-netinst.iso" && s.Url.Host == "ftp.fau.de");
    }

    [Fact]
    public async Task Resolve_Live_UsesTheLiveDirectory()
    {
        var provider = Provider(DebianSite());
        var variant = (await provider.ListVariantsAsync("debian", CancellationToken.None)).First(v => v.Id == "13.7.0/live-gnome");

        var request = await provider.ResolveAsync(variant, null, CancellationToken.None);

        Assert.Equal(new Uri(Live + "debian-live-13.7.0-amd64-gnome.iso"), request.Url);
        Assert.Equal("e94859a83b305cae5125dc9c6080ced040c59d099ea7381e7f300fd1dba87a17", request.ExpectedHashes[0].Hex);
        Assert.Contains(request.Sources, s => s.Url.AbsolutePath == "/debian-cd/current-live/amd64/iso-hybrid/debian-live-13.7.0-amd64-gnome.iso");
    }

    [Fact]
    public async Task Resolve_RejectsAForgedChecksumFile()
    {
        var web = DebianSite();
        var provider = Provider(web);
        var variant = (await provider.ListVariantsAsync("debian", CancellationToken.None)).First(v => v.IsRecommended);
        web.Serve(Netinst + "SHA256SUMS", DistroFixtures.Text("debian/netinst.SHA256SUMS").Replace("a7ef94ac", "00000000", StringComparison.Ordinal));

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ResolveAsync(variant, "x64", CancellationToken.None));

        Assert.Equal(ErrorCode.SignatureInvalid, error.Code);
    }

    [Fact]
    public async Task Resolve_RejectsASignatureMadeByUbuntusKey()
    {
        var web = DebianSite();
        var provider = Provider(web);
        var variant = (await provider.ListVariantsAsync("debian", CancellationToken.None)).First(v => v.IsRecommended);
        web.ServeFixture(Netinst + "SHA256SUMS", "ubuntu/ubuntu-26.04.SHA256SUMS");
        web.ServeFixture(Netinst + "SHA256SUMS.sign", "ubuntu/ubuntu-26.04.SHA256SUMS.gpg");

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ResolveAsync(variant, "x64", CancellationToken.None));

        Assert.Equal(ErrorCode.SignatureInvalid, error.Code);
    }

    [Fact]
    public async Task Resolve_WhenTheArmDirectoryIsNotPublishedYet_ReportsCatalogUnavailable()
    {
        var web = DebianSite().Fail(NetinstArm + "SHA256SUMS", HttpStatusCode.NotFound);
        var provider = Provider(web);
        var variant = (await provider.ListVariantsAsync("debian", CancellationToken.None)).First(v => v.IsRecommended);

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ResolveAsync(variant, "arm64", CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
    }

    [Fact]
    public async Task Resolve_NeedsAnArchitectureWhenTheVariantHasTwo()
    {
        var provider = Provider(DebianSite());
        var variant = (await provider.ListVariantsAsync("debian", CancellationToken.None)).First(v => v.IsRecommended);

        await Assert.ThrowsAsync<ArgumentException>(() => provider.ResolveAsync(variant, null, CancellationToken.None));
    }

    [Fact]
    public async Task Variants_AreSigned_AndTheProductIsDebian()
    {
        var provider = Provider(DebianSite());

        var product = Assert.Single(await provider.ListProductsAsync(CancellationToken.None));
        var variants = await provider.ListVariantsAsync(product.Id, CancellationToken.None);

        Assert.Equal(CatalogFamily.Linux, product.Family);
        Assert.All(variants, v => Assert.Equal(DistroProperties.PinnedKey, v.Properties[DistroProperties.HashTrust]));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ListVariantsAsync("ubuntu", CancellationToken.None));
    }
}
