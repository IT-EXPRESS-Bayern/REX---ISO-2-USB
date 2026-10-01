// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using Bootrix.Core.Catalog;
using Bootrix.Core.Catalog.Distros;
using Bootrix.Core.Errors;
using Bootrix.Core.Net;
using Bootrix.Core.Tests.Catalog.Distros.Support;
using Microsoft.Extensions.Time.Testing;

namespace Bootrix.Core.Tests.Catalog.Distros;

public class ProxmoxProviderTests
{
    private const string Site = "https://enterprise.proxmox.com/iso/";

    private readonly FakeTimeProvider _time = new(DistroFixtures.CapturedOn);

    private static FakeWeb ProxmoxSite() => new FakeWeb().ServeFixture(Site + "SHA256SUMS", "proxmox/SHA256SUMS");

    private ProxmoxProvider Provider(FakeWeb web) => new(web.CreateClient(), _time);

    [Fact]
    public void Images_AreFoundForAllFourProducts_AndTheLegacyMailGatewayNameIsLeftOut()
    {
        var images = ProxmoxImages.Parse(ChecksumFile.Parse(DistroFixtures.Text("proxmox/SHA256SUMS")));

        Assert.Equal(
            ["proxmox-backup-server", "proxmox-datacenter-manager", "proxmox-mail-gateway", "proxmox-ve"],
            images.Select(i => i.Product).Distinct().Order());
        // The file has 15 lines; "proxmox-mailgateway_7.3-1.iso" is the old spelling of the product and not offered.
        Assert.Equal(14, images.Count);
        Assert.DoesNotContain(images, i => i.FileName.Contains("mailgateway", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ve_ListsEveryHostedReleaseNewestFirst_WithTheArmImageOfTheNewestOne()
    {
        var variants = await Provider(ProxmoxSite()).ListVariantsAsync("proxmox-ve", CancellationToken.None);

        Assert.Equal(["9.2-1", "9.1-1", "8.4-1", "7.4-1"], variants.Select(v => v.Id));
        Assert.Equal("Proxmox VE 9.2-1", variants[0].Name);
        Assert.Equal(["x64", "arm64"], variants[0].Architectures);
        Assert.Equal(["x64"], variants[1].Architectures);
        Assert.Equal(["9.2-1"], variants.Where(v => v.IsRecommended).Select(v => v.Id));
    }

    [Theory]
    [InlineData("proxmox-backup-server", new[] { "4.2-1", "4.1-1", "3.4-1", "2.4-1" })]
    [InlineData("proxmox-mail-gateway", new[] { "9.1-1", "9.0-1", "8.2-1" })]
    [InlineData("proxmox-datacenter-manager", new[] { "1.1-1", "1.0-2" })]
    public async Task OtherProducts_AreListedFromTheSameChecksumFile(string product, string[] expected)
    {
        var variants = await Provider(ProxmoxSite()).ListVariantsAsync(product, CancellationToken.None);

        Assert.Equal(expected, variants.Select(v => v.Id));
        Assert.All(variants, v => Assert.Equal(DistroProperties.TlsOnly, v.Properties[DistroProperties.HashTrust]));
    }

    [Fact]
    public async Task Resolve_UsesTheDigestOfTheChosenArchitecture()
    {
        var provider = Provider(ProxmoxSite());
        var variant = (await provider.ListVariantsAsync("proxmox-ve", CancellationToken.None))[0];

        var x64 = await provider.ResolveAsync(variant, "x64", CancellationToken.None);
        var arm = await provider.ResolveAsync(variant, "arm64", CancellationToken.None);

        Assert.Equal(new Uri(Site + "proxmox-ve_9.2-1.iso"), x64.Url);
        Assert.Equal(new FileHash(HashKind.Sha256, "4e88fe416df9b527624a175f24c9aa07c714d3332afb1ee3dbf3879573ef2c6c"), Assert.Single(x64.ExpectedHashes));
        Assert.Equal(new Uri(Site + "proxmox-ve_9.2-1-arm64.iso"), arm.Url);
        Assert.Equal("b1619dcd1f5b1a6d67d77b59e7e2fa2033174551d1e1b9dc22b2171ec093abbd", arm.ExpectedHashes[0].Hex);
    }

    [Fact]
    public async Task Resolve_OfAnOlderRelease_NeedsNoArchitecture()
    {
        var provider = Provider(ProxmoxSite());
        var variant = (await provider.ListVariantsAsync("proxmox-ve", CancellationToken.None)).First(v => v.Id == "8.4-1");

        var request = await provider.ResolveAsync(variant, null, CancellationToken.None);

        Assert.Equal("d237d70ca48a9f6eb47f95fd4fd337722c3f69f8106393844d027d28c26523d8", request.ExpectedHashes[0].Hex);
    }

    [Fact]
    public async Task Resolve_WhenTheImageWasRemoved_ReportsTheMissingEntry()
    {
        var web = ProxmoxSite();
        var provider = Provider(web);
        var variant = (await provider.ListVariantsAsync("proxmox-ve", CancellationToken.None)).First(v => v.Id == "7.4-1");
        web.Serve(Site + "SHA256SUMS", DistroFixtures.Text("proxmox/SHA256SUMS").Replace("proxmox-ve_7.4-1.iso", "proxmox-ve_7.4-1.old", StringComparison.Ordinal));
        _time.Advance(TimeSpan.FromMinutes(11));

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ResolveAsync(variant, null, CancellationToken.None));

        Assert.Equal(ErrorCode.ChecksumFileInvalid, error.Code);
    }

    [Fact]
    public async Task Variants_WhenTheServerIsDown_ReportCatalogUnavailable()
    {
        var web = new FakeWeb().Fail(Site + "SHA256SUMS", HttpStatusCode.BadGateway);

        var error = await Assert.ThrowsAsync<BootrixException>(() => Provider(web).ListVariantsAsync("proxmox-ve", CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
    }

    [Fact]
    public async Task Products_AreTheFourProxmoxProducts_AndAnotherIsACallerMistake()
    {
        var provider = Provider(ProxmoxSite());

        var products = await provider.ListProductsAsync(CancellationToken.None);

        Assert.Equal(["proxmox-ve", "proxmox-backup-server", "proxmox-mail-gateway", "proxmox-datacenter-manager"], products.Select(p => p.Id));
        Assert.All(products, p => Assert.Equal(CatalogFamily.Linux, p.Family));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ListVariantsAsync("vmware", CancellationToken.None));
    }
}
