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

public class FreeBsdProviderTests
{
    private const string Root = "https://download.freebsd.org/releases/ISO-IMAGES/";

    private readonly FakeTimeProvider _time = new(DistroFixtures.CapturedOn);

    private static FakeWeb FreeBsdSite() => new FakeWeb()
        .ServeFixture(Root, "freebsd/iso-images.html")
        .ServeFixture(Root + "15.1/", "freebsd/iso-images-15.1.html")
        .ServeFixture(Root + "14.5/", "freebsd/iso-images-14.5.html")
        .ServeFixture(Root + "15.1/CHECKSUM.SHA256-FreeBSD-15.1-RELEASE-amd64", "freebsd/CHECKSUM.SHA256-FreeBSD-15.1-RELEASE-amd64")
        .ServeFixture(Root + "15.1/CHECKSUM.SHA256-FreeBSD-15.1-RELEASE-arm64-aarch64", "freebsd/CHECKSUM.SHA256-FreeBSD-15.1-RELEASE-arm64-aarch64")
        .ServeFixture(Root + "14.5/CHECKSUM.SHA256-FreeBSD-14.5-RELEASE-amd64", "freebsd/CHECKSUM.SHA256-FreeBSD-14.5-RELEASE-amd64");

    private FreeBsdProvider Provider(FakeWeb web) => new(web.CreateClient(), _time);

    [Fact]
    public void Releases_AreTheNewestOfEachOfTheNewestBranches()
    {
        var listing = DirectoryListing.Parse(DistroFixtures.Text("freebsd/iso-images.html"));

        // 14.4, 14.5, 15.0 and 15.1 are on the server; 14.4 and 15.0 were replaced by the next minor release.
        Assert.Equal(["15.1", "14.5"], FreeBsdImages.Releases(listing, 2).Select(v => v.ToString()));
        Assert.Equal(["15.1"], FreeBsdImages.Releases(listing, 1).Select(v => v.ToString()));
    }

    [Fact]
    public void Images_AreTheUncompressedOnesForPcsAndArm64()
    {
        var images = FreeBsdImages.Parse(DirectoryListing.Parse(DistroFixtures.Text("freebsd/iso-images-15.1.html")), "15.1");

        Assert.Equal(10, images.Count);
        Assert.DoesNotContain(images, i => i.FileName.EndsWith(".xz", StringComparison.Ordinal));
        var memstick = Assert.Single(images, i => i is { Kind: "memstick", Architecture: "x64" });
        Assert.Equal("FreeBSD-15.1-RELEASE-amd64-memstick.img", memstick.FileName);
        Assert.Equal(1_552_601_600, memstick.Size);
        Assert.Equal("arm64-aarch64", Assert.Single(images, i => i is { Kind: "dvd1", Architecture: "arm64" }).VendorArchitecture);
    }

    [Fact]
    public async Task Variants_ArePerReleaseAndKindWithBothArchitectures()
    {
        var variants = await Provider(FreeBsdSite()).ListVariantsAsync("freebsd", CancellationToken.None);

        Assert.Equal(
            [
                "15.1/memstick", "15.1/mini-memstick", "15.1/disc1", "15.1/dvd1", "15.1/bootonly",
                "14.5/memstick", "14.5/mini-memstick", "14.5/disc1", "14.5/dvd1", "14.5/bootonly",
            ],
            variants.Select(v => v.Id));
        var memstick = variants[0];
        Assert.Equal("FreeBSD 15.1 (USB memstick image)", memstick.Name);
        Assert.Equal(["x64", "arm64"], memstick.Architectures);
        Assert.Equal(1_552_601_600, memstick.SizeBytes);
        Assert.Equal(CatalogFamily.Bsd, (await Provider(FreeBsdSite()).ListProductsAsync(CancellationToken.None))[0].Family);
    }

    [Fact]
    public async Task OnlyTheNewestMemstickIsRecommended()
    {
        var variants = await Provider(FreeBsdSite()).ListVariantsAsync("freebsd", CancellationToken.None);

        Assert.Equal(["15.1/memstick"], variants.Where(v => v.IsRecommended).Select(v => v.Id));
    }

    [Fact]
    public async Task Variants_AreOnlyTlsProtected()
    {
        var variants = await Provider(FreeBsdSite()).ListVariantsAsync("freebsd", CancellationToken.None);

        Assert.All(variants, v => Assert.Equal(DistroProperties.TlsOnly, v.Properties[DistroProperties.HashTrust]));
    }

    [Fact]
    public async Task Resolve_UsesTheBsdStyleDigestAndTheExactSize()
    {
        var provider = Provider(FreeBsdSite());
        var variant = (await provider.ListVariantsAsync("freebsd", CancellationToken.None)).First(v => v.Id == "15.1/memstick");

        var request = await provider.ResolveAsync(variant, "x64", CancellationToken.None);

        Assert.Equal(new Uri(Root + "15.1/FreeBSD-15.1-RELEASE-amd64-memstick.img"), request.Url);
        Assert.Equal(new FileHash(HashKind.Sha256, "26c6d5de1156e7a99df920d047578b504c4899865d91b1156b1de1a0c5239eae"), Assert.Single(request.ExpectedHashes));
        Assert.Equal(1_552_601_600, request.ExpectedSize);
        Assert.Contains(request.Sources, s => s.Url == new Uri("https://ftp.fau.de/freebsd/releases/ISO-IMAGES/15.1/FreeBSD-15.1-RELEASE-amd64-memstick.img") && s.Location == "DE");
    }

    [Fact]
    public async Task Resolve_DoesNotMixUpAnImageWithItsCompressedCopy()
    {
        var provider = Provider(FreeBsdSite());
        var variant = (await provider.ListVariantsAsync("freebsd", CancellationToken.None)).First(v => v.Id == "15.1/bootonly");

        var request = await provider.ResolveAsync(variant, "x64", CancellationToken.None);

        // The checksum file lists bootonly.iso and bootonly.iso.xz one after the other.
        Assert.Equal("3e74120a59512cefc35840443bdd05087c8a010a27cf8a6fbb4f7450824f092e", request.ExpectedHashes[0].Hex);
    }

    [Fact]
    public async Task Resolve_Arm64_ReadsTheAarch64ChecksumFile()
    {
        var provider = Provider(FreeBsdSite());
        var variant = (await provider.ListVariantsAsync("freebsd", CancellationToken.None)).First(v => v.Id == "15.1/disc1");

        var request = await provider.ResolveAsync(variant, "arm64", CancellationToken.None);

        Assert.EndsWith("FreeBSD-15.1-RELEASE-arm64-aarch64-disc1.iso", request.Url.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(1_196_779_520, request.ExpectedSize);
    }

    [Fact]
    public async Task Resolve_OlderBranch_UsesItsOwnChecksumFile()
    {
        var provider = Provider(FreeBsdSite());
        var variant = (await provider.ListVariantsAsync("freebsd", CancellationToken.None)).First(v => v.Id == "14.5/disc1");

        var request = await provider.ResolveAsync(variant, "x64", CancellationToken.None);

        Assert.Equal("a7f360c8dbd4727a8d3213ac85f6eb9b51ee4ed2577a2c31aaebbcae3d921d56", request.ExpectedHashes[0].Hex);
    }

    [Fact]
    public async Task Resolve_WhenTheChecksumFileDoesNotNameTheImage_ReportsTheMissingEntry()
    {
        var web = FreeBsdSite().ServeFixture(Root + "15.1/CHECKSUM.SHA256-FreeBSD-15.1-RELEASE-amd64", "freebsd/CHECKSUM.SHA256-FreeBSD-14.5-RELEASE-amd64");
        var provider = Provider(web);
        var variant = (await provider.ListVariantsAsync("freebsd", CancellationToken.None)).First(v => v.Id == "15.1/memstick");

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ResolveAsync(variant, "x64", CancellationToken.None));

        Assert.Equal(ErrorCode.ChecksumFileInvalid, error.Code);
    }

    [Fact]
    public async Task Variants_WhenTheServerIsDown_ReportCatalogUnavailable()
    {
        var web = new FakeWeb().Fail(Root, HttpStatusCode.ServiceUnavailable);

        var error = await Assert.ThrowsAsync<BootrixException>(() => Provider(web).ListVariantsAsync("freebsd", CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
    }

    [Fact]
    public async Task UnknownProduct_IsACallerMistake()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Provider(FreeBsdSite()).ListVariantsAsync("openbsd", CancellationToken.None));
    }
}
