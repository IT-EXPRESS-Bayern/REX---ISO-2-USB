// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using Bootrix.Core.Catalog;
using Bootrix.Core.Catalog.Distros;
using Bootrix.Core.Errors;
using Bootrix.Core.Net;
using Bootrix.Core.Tests.Catalog.Distros.Support;
using Microsoft.Extensions.Time.Testing;

namespace Bootrix.Core.Tests.Catalog.Distros;

public class Memtest86PlusProviderTests
{
    private const string Front = "https://www.memtest.org/";
    private const string Release = "https://memtest.org/download/v8.10/";

    private readonly FakeTimeProvider _time = new(DistroFixtures.CapturedOn);

    private static FakeWeb MemtestSite() => new FakeWeb()
        .ServeFixture(Front, "memtest/front-page-downloads.html")
        .ServeFixture(Release + "sha256sum.txt", "memtest/sha256sum.txt");

    private Memtest86PlusProvider Provider(FakeWeb web) => new(web.CreateClient(), _time);

    [Fact]
    public void Downloads_AreTheThreeIsoBuildsOfTheFrontPage_NotTheInstallerBinariesOrSources()
    {
        var downloads = MemtestDownloads.Parse(DistroFixtures.Text("memtest/front-page-downloads.html"));

        Assert.Equal(
            ["mt86plus_8.10_x86_64.iso.zip", "mt86plus_8.10_x86_64.grub.iso.zip", "mt86plus_8.10_i586.iso.zip"],
            downloads.Select(d => d.FileName));
        Assert.All(downloads, d => Assert.Equal("8.10", d.Version));
        Assert.Equal(["memtest.iso", "grub-memtest.iso", "memtest.iso"], downloads.Select(d => d.ImageInArchive));
        Assert.Equal(["x64", "x64", "i386"], downloads.Select(d => d.Architecture));
    }

    [Fact]
    public async Task Variants_NameTheBuildAndRecommendThe64BitOne()
    {
        var variants = await Provider(MemtestSite()).ListVariantsAsync("memtest86plus", CancellationToken.None);

        Assert.Equal(["8.10/x86_64", "8.10/x86_64-grub", "8.10/i586"], variants.Select(v => v.Id));
        Assert.Equal(
            ["Memtest86+ 8.10 (64-bit)", "Memtest86+ 8.10 (64-bit, with GRUB)", "Memtest86+ 8.10 (32-bit)"],
            variants.Select(v => v.Name));
        Assert.Equal(["8.10/x86_64"], variants.Where(v => v.IsRecommended).Select(v => v.Id));
        Assert.Equal(["i386"], variants[2].Architectures);
    }

    [Fact]
    public async Task Variants_SayThatTheIsoIsInsideAZip_AndThatTheDigestIsOnlyTlsProtected()
    {
        var variants = await Provider(MemtestSite()).ListVariantsAsync("memtest86plus", CancellationToken.None);

        Assert.All(variants, v =>
        {
            Assert.Equal("zip", v.Properties[DistroProperties.Archive]);
            Assert.Equal(DistroProperties.TlsOnly, v.Properties[DistroProperties.HashTrust]);
        });
        Assert.Equal("memtest.iso", variants[0].Properties[DistroProperties.ArchiveEntry]);
        Assert.Equal("grub-memtest.iso", variants[1].Properties[DistroProperties.ArchiveEntry]);
    }

    [Fact]
    public async Task Resolve_ReadsTheDigestOfTheZipFromThePrefixedChecksumList()
    {
        var provider = Provider(MemtestSite());
        var variants = await provider.ListVariantsAsync("memtest86plus", CancellationToken.None);

        var plain = await provider.ResolveAsync(variants[0], null, CancellationToken.None);
        var grub = await provider.ResolveAsync(variants[1], null, CancellationToken.None);

        Assert.Equal(new Uri(Release + "mt86plus_8.10_x86_64.iso.zip"), plain.Url);
        Assert.Equal(new FileHash(HashKind.Sha256, "93530005d6ac6a85aa2a49c68604a43c25794ecccf796c4f8849a73a8001be9a"), Assert.Single(plain.ExpectedHashes));
        // "...x86_64.grub.iso.zip" must not be mistaken for "...x86_64.iso.zip" by the path-suffix match.
        Assert.Equal("49a7ee7982d4683b8775ee9e79f499fc33d9ff0aac0a824243ab8462e2d14881", grub.ExpectedHashes[0].Hex);
    }

    [Fact]
    public async Task Resolve_WhenTheListDoesNotNameTheFile_ReportsTheMissingEntry()
    {
        var web = MemtestSite().Serve(Release + "sha256sum.txt", DistroFixtures.Text("memtest/sha256sum.txt").Replace("_x86_64.iso.zip", "_x86_64.iso.old", StringComparison.Ordinal));
        var provider = Provider(web);
        var variant = (await provider.ListVariantsAsync("memtest86plus", CancellationToken.None))[0];

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ResolveAsync(variant, null, CancellationToken.None));

        Assert.Equal(ErrorCode.ChecksumFileInvalid, error.Code);
    }

    [Fact]
    public async Task Variants_WhenThePageHasNoDownloads_AreEmpty_AndWhenItIsDownTheCatalogIsUnavailable()
    {
        var empty = new FakeWeb().Serve(Front, "<html>redesigned</html>");
        Assert.Empty(await Provider(empty).ListVariantsAsync("memtest86plus", CancellationToken.None));

        var down = new FakeWeb().Fail(Front, HttpStatusCode.BadGateway);
        var error = await Assert.ThrowsAsync<BootrixException>(() => Provider(down).ListVariantsAsync("memtest86plus", CancellationToken.None));
        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
    }

    [Fact]
    public async Task Product_IsAUtility()
    {
        var provider = Provider(MemtestSite());

        var product = Assert.Single(await provider.ListProductsAsync(CancellationToken.None));

        Assert.Equal(CatalogFamily.Utility, product.Family);
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ListVariantsAsync("memtest86", CancellationToken.None));
    }
}
