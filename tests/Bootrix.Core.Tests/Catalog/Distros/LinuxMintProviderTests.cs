// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using Bootrix.Core.Catalog;
using Bootrix.Core.Catalog.Distros;
using Bootrix.Core.Catalog.Distros.Common;
using Bootrix.Core.Errors;
using Bootrix.Core.Net;
using Bootrix.Core.Tests.Catalog.Distros.Support;
using Microsoft.Extensions.Time.Testing;

namespace Bootrix.Core.Tests.Catalog.Distros;

public class LinuxMintProviderTests
{
    private const string Stable = "https://mirrors.edge.kernel.org/linuxmint/stable/";

    private readonly FakeTimeProvider _time = new(DistroFixtures.CapturedOn);

    private static FakeWeb MintSite() => new FakeWeb()
        .ServeFixture(Stable, "mint/stable-index.html")
        .ServeFixture(Stable + "22.3/sha256sum.txt", "mint/22.3.sha256sum.txt")
        .ServeFixture(Stable + "22.3/sha256sum.txt.gpg", "mint/22.3.sha256sum.txt.gpg")
        .ServeFixture(Stable + "21.3/sha256sum.txt", "mint/21.3.sha256sum.txt")
        .ServeFixture(Stable + "21.3/sha256sum.txt.gpg", "mint/21.3.sha256sum.txt.gpg");

    private LinuxMintProvider Provider(FakeWeb web) => new(web.CreateClient(), _time);

    [Fact]
    public async Task Variants_AreTheEditionsOfTheNewestPointReleaseOfTheTwoNewestSeries()
    {
        var variants = await Provider(MintSite()).ListVariantsAsync("linuxmint", CancellationToken.None);

        // 22.0, 22.1 and 22.2 are older point releases of the 22 series and 20.x is past its support.
        Assert.Equal(
            ["22.3/cinnamon", "22.3/mate", "22.3/xfce", "21.3/cinnamon", "21.3/mate", "21.3/xfce", "21.3/cinnamon-edge"],
            variants.Select(v => v.Id));
    }

    [Fact]
    public async Task Variants_NameTheEditionAndMarkTheEdgeImage()
    {
        var variants = await Provider(MintSite()).ListVariantsAsync("linuxmint", CancellationToken.None);

        Assert.Equal("Linux Mint 22.3 Cinnamon", variants.First(v => v.Id == "22.3/cinnamon").Name);
        Assert.Equal("Linux Mint 21.3 Cinnamon (Edge)", variants.First(v => v.Id == "21.3/cinnamon-edge").Name);
        Assert.Equal("Linux Mint 22.3 Xfce", variants.First(v => v.Id == "22.3/xfce").Name);
    }

    [Fact]
    public async Task OnlyTheNewestCinnamonImageIsRecommended()
    {
        var variants = await Provider(MintSite()).ListVariantsAsync("linuxmint", CancellationToken.None);

        Assert.Equal(["22.3/cinnamon"], variants.Where(v => v.IsRecommended).Select(v => v.Id));
    }

    [Fact]
    public async Task Resolve_VerifiesTheSignatureAndSpreadsOverMirrors()
    {
        var provider = Provider(MintSite());
        var variant = (await provider.ListVariantsAsync("linuxmint", CancellationToken.None)).First(v => v.Id == "22.3/mate");

        var request = await provider.ResolveAsync(variant, null, CancellationToken.None);

        Assert.Equal(new Uri(Stable + "22.3/linuxmint-22.3-mate-64bit.iso"), request.Url);
        Assert.Equal("7609294da613b75eea89bb918292125e9f06418a368136fb190466e15bf8c373", Assert.Single(request.ExpectedHashes).Hex);
        Assert.Contains(request.Sources, s => s.Url == new Uri("https://ftp.fau.de/mint/iso/stable/22.3/linuxmint-22.3-mate-64bit.iso") && s.Location == "DE");
        Assert.Contains(request.Sources, s => s.Url.Host == "mirror.csclub.uwaterloo.ca" && s.Location == "CA");
        Assert.Equal(5, request.Sources.Count);
    }

    [Fact]
    public async Task Resolve_EdgeImage_UsesItsOwnDigest()
    {
        var provider = Provider(MintSite());
        var variant = (await provider.ListVariantsAsync("linuxmint", CancellationToken.None)).First(v => v.Id == "21.3/cinnamon-edge");

        var request = await provider.ResolveAsync(variant, null, CancellationToken.None);

        Assert.EndsWith("linuxmint-21.3-cinnamon-64bit-edge.iso", request.Url.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("ac79f36b82896e74299fa6dd1f40f00648ca2160903fea5d4d138db99fc6ad4e", request.ExpectedHashes[0].Hex);
    }

    [Fact]
    public async Task Resolve_RejectsAChecksumFileWithoutAValidSignature()
    {
        var web = MintSite();
        var provider = Provider(web);
        var variant = (await provider.ListVariantsAsync("linuxmint", CancellationToken.None)).First(v => v.Id == "22.3/xfce");
        web.Serve(Stable + "22.3/sha256sum.txt", DistroFixtures.Text("mint/22.3.sha256sum.txt").Replace("45a835b5", "00000000", StringComparison.Ordinal));

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ResolveAsync(variant, null, CancellationToken.None));

        Assert.Equal(ErrorCode.SignatureInvalid, error.Code);
    }

    [Fact]
    public async Task Resolve_RejectsASignatureOfAnotherRelease()
    {
        var web = MintSite();
        var provider = Provider(web);
        var variant = (await provider.ListVariantsAsync("linuxmint", CancellationToken.None)).First(v => v.Id == "22.3/xfce");
        web.ServeFixture(Stable + "22.3/sha256sum.txt.gpg", "mint/21.3.sha256sum.txt.gpg");

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ResolveAsync(variant, null, CancellationToken.None));

        Assert.Equal(ErrorCode.SignatureInvalid, error.Code);
    }

    [Fact]
    public async Task Variants_WhenTheMirrorIsDown_ReportCatalogUnavailable()
    {
        var web = new FakeWeb().Fail(Stable, HttpStatusCode.BadGateway);

        var error = await Assert.ThrowsAsync<BootrixException>(() => Provider(web).ListVariantsAsync("linuxmint", CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
    }

    [Fact]
    public void ReleaseDirectories_SkipFilesAndNonVersionNames()
    {
        var listing = new[]
        {
            new ListingEntry("22.3", true, null),
            new ListingEntry("22.10", true, null),
            new ListingEntry("debian", true, null),
            new ListingEntry("sha256sum.txt", false, null),
            new ListingEntry("23", true, null),
        };

        Assert.Equal(["23", "22.10", "22.3"], LinuxMintImages.ReleaseDirectories(listing).Select(v => v.ToString()));
        Assert.Equal(["23", "22.10"], LinuxMintImages.NewestOfEachSeries(LinuxMintImages.ReleaseDirectories(listing), 2).Select(v => v.ToString()));
    }

    [Fact]
    public async Task Products_AreJustMint_AndAnUnknownOneIsACallerMistake()
    {
        var provider = Provider(MintSite());

        var product = Assert.Single(await provider.ListProductsAsync(CancellationToken.None));
        Assert.Equal(CatalogFamily.Linux, product.Family);
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ListVariantsAsync("lmde", CancellationToken.None));
    }
}
