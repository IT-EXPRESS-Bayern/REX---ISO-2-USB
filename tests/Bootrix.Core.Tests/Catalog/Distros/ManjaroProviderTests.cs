// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using Bootrix.Core.Catalog;
using Bootrix.Core.Catalog.Distros;
using Bootrix.Core.Errors;
using Bootrix.Core.Net;
using Bootrix.Core.Tests.Catalog.Distros.Support;
using Microsoft.Extensions.Time.Testing;

namespace Bootrix.Core.Tests.Catalog.Distros;

public class ManjaroProviderTests
{
    private const string Info = "https://gitlab.manjaro.org/web/iso-info/-/raw/master/file-info.json";

    private readonly FakeTimeProvider _time = new(DistroFixtures.CapturedOn);

    private static FakeWeb ManjaroSite() => new FakeWeb()
        .ServeFixture(Info, "manjaro/file-info.json")
        .ServeFixture("https://download.manjaro.org/kde/26.1.1/manjaro-kde-26.1.1-260825-linux71.iso.sha256", "manjaro/kde.sha256")
        .ServeFixture("https://download.manjaro.org/kde/26.1.1/manjaro-kde-26.1.1-minimal-260825-linux71.iso.sha256", "manjaro/kde-minimal.sha256")
        .ServeFixture("https://download.manjaro.org/xfce/26.1.1/manjaro-xfce-26.1.1-260825-linux71.iso.sha256", "manjaro/xfce.sha256")
        .ServeFixture("https://download.manjaro.org/cinnamon/25.0.3/manjaro-cinnamon-25.0.3-250609-linux612.iso.sha512", "manjaro/cinnamon.sha512");

    private ManjaroProvider Provider(FakeWeb web) => new(web.CreateClient(), _time);

    [Fact]
    public void FileInfo_ListsOfficialAndCommunityEditionsButNotTheArmBoards()
    {
        var editions = ManjaroReleases.Parse(DistroFixtures.Text("manjaro/file-info.json"));

        Assert.Equal(["plasma", "xfce", "gnome", "cinnamon", "i3", "sway"], editions.Select(e => e.Key));
        Assert.Equal(["official", "official", "official", "community", "community", "community"], editions.Select(e => e.Group));
        var plasma = editions[0];
        Assert.Equal("manjaro-kde-26.1.1-260825-linux71.iso", plasma.Full!.FileName);
        Assert.Equal("26.1.1", plasma.Full.Version);
        Assert.Equal(new DateOnly(2026, 8, 25), plasma.Full.Date);
        Assert.EndsWith(".iso.sig", plasma.Full.Signature!.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("manjaro-kde-26.1.1-minimal-260825-linux71.iso", plasma.Minimal!.FileName);
        Assert.Equal("https://manjaro-sway.download/", editions[5].CustomUrl!.AbsoluteUri);
        Assert.Null(editions[5].Full);
    }

    [Fact]
    public async Task Variants_AreFullAndMinimalImages_AndCarryTheBuildDate()
    {
        var variants = await Provider(ManjaroSite()).ListVariantsAsync("manjaro", CancellationToken.None);

        Assert.Equal(
            ["plasma", "plasma-minimal", "xfce", "xfce-minimal", "gnome", "gnome-minimal", "cinnamon", "cinnamon-minimal", "i3", "i3-minimal", "sway"],
            variants.Select(v => v.Id));
        var plasma = variants[0];
        Assert.Equal("Manjaro KDE Plasma", plasma.Name);
        Assert.Equal("26.1.1", plasma.Version);
        Assert.Equal(new DateOnly(2026, 8, 25), plasma.ReleaseDate);
        Assert.Equal(["x64"], plasma.Architectures);
        Assert.Equal("Manjaro KDE Plasma (minimal)", variants[1].Name);
        Assert.Equal("Manjaro Cinnamon (community)", variants[6].Name);
        Assert.Equal("Manjaro Cinnamon (community, minimal)", variants[7].Name);
    }

    [Fact]
    public async Task OnlyTheOfficialPlasmaImageIsRecommended()
    {
        var variants = await Provider(ManjaroSite()).ListVariantsAsync("manjaro", CancellationToken.None);

        Assert.Equal(["plasma"], variants.Where(v => v.IsRecommended).Select(v => v.Id));
    }

    [Fact]
    public async Task Variants_AreOnlyTlsProtected()
    {
        var variants = await Provider(ManjaroSite()).ListVariantsAsync("manjaro", CancellationToken.None);

        Assert.All(variants, v => Assert.Equal(DistroProperties.TlsOnly, v.Properties[DistroProperties.HashTrust]));
        Assert.Equal("official", variants[0].Properties["group"]);
        Assert.EndsWith(".iso.sig", variants[0].Properties["signature"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task SpinWithItsOwnSite_IsAManualDownload()
    {
        var sway = (await Provider(ManjaroSite()).ListVariantsAsync("manjaro", CancellationToken.None)).Single(v => v.Id == "sway");

        Assert.Equal("https://manjaro-sway.download/", sway.ManualUrl);
        Assert.Equal("Manjaro Sway (community)", sway.Name);
        await Assert.ThrowsAsync<NotSupportedException>(() => Provider(ManjaroSite()).ResolveAsync(sway, null, CancellationToken.None));
    }

    [Fact]
    public async Task Resolve_ReadsTheSha256FileBesideTheImage()
    {
        var provider = Provider(ManjaroSite());
        var variants = await provider.ListVariantsAsync("manjaro", CancellationToken.None);

        var request = await provider.ResolveAsync(variants.Single(v => v.Id == "plasma"), null, CancellationToken.None);

        Assert.Equal(new Uri("https://download.manjaro.org/kde/26.1.1/manjaro-kde-26.1.1-260825-linux71.iso"), Assert.Single(request.Sources).Url);
        Assert.Equal(new FileHash(HashKind.Sha256, "f60be571f9c5509a0d96afea7d3e1da70fd1ac8d9a0214b0193be1641080ab95"), Assert.Single(request.ExpectedHashes));
    }

    [Fact]
    public async Task Resolve_MinimalImage_UsesItsOwnDigest()
    {
        var provider = Provider(ManjaroSite());
        var variants = await provider.ListVariantsAsync("manjaro", CancellationToken.None);

        var request = await provider.ResolveAsync(variants.Single(v => v.Id == "plasma-minimal"), "x64", CancellationToken.None);

        Assert.Equal("1ce53bce2f26994aa651f27bb4ad0512662e274c89d4ab666bcf264a54e3e53e", request.ExpectedHashes[0].Hex);
    }

    [Fact]
    public async Task Resolve_CommunityImage_UsesTheSha512File()
    {
        var provider = Provider(ManjaroSite());
        var variants = await provider.ListVariantsAsync("manjaro", CancellationToken.None);

        var request = await provider.ResolveAsync(variants.Single(v => v.Id == "cinnamon"), null, CancellationToken.None);

        var hash = Assert.Single(request.ExpectedHashes);
        Assert.Equal(HashKind.Sha512, hash.Kind);
        Assert.StartsWith("df01b9c5", hash.Hex, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_WhenTheChecksumFileDoesNotNameTheImage_ReportsTheMissingEntry()
    {
        var web = ManjaroSite().ServeFixture("https://download.manjaro.org/kde/26.1.1/manjaro-kde-26.1.1-260825-linux71.iso.sha256", "manjaro/xfce.sha256");
        var provider = Provider(web);
        var variants = await provider.ListVariantsAsync("manjaro", CancellationToken.None);

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ResolveAsync(variants.Single(v => v.Id == "plasma"), null, CancellationToken.None));

        Assert.Equal(ErrorCode.ChecksumFileInvalid, error.Code);
    }

    [Fact]
    public async Task Resolve_WhenTheChecksumFileIsGone_ReportsCatalogUnavailable()
    {
        var web = ManjaroSite().Fail("https://download.manjaro.org/kde/26.1.1/manjaro-kde-26.1.1-260825-linux71.iso.sha256", HttpStatusCode.NotFound);
        var provider = Provider(web);
        var variants = await provider.ListVariantsAsync("manjaro", CancellationToken.None);

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ResolveAsync(variants.Single(v => v.Id == "plasma"), null, CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
    }

    [Fact]
    public void EntriesWithoutHttpsLinks_AreIgnored()
    {
        const string json = """
            {"official":{"a":{"image":"http://x.example/manjaro-a-1.0-260101-linux70.iso","checksum":"https://x.example/a.sha256"},
                         "b":{"image":"https://x.example/manjaro-b-1.0-260101-linux70.iso","checksum":"ftp://x.example/b.sha256"},
                         "c":{"image":"https://x.example/manjaro-c-1.0-260101-linux70.iso","checksum":"https://x.example/c.sha256"}}}
            """;

        var edition = Assert.Single(ManjaroReleases.Parse(json));

        Assert.Equal("c", edition.Key);
    }

    [Fact]
    public async Task Variants_WhenTheListIsNotJson_ReportCatalogUnavailable()
    {
        var web = new FakeWeb().Serve(Info, "<html>");

        var error = await Assert.ThrowsAsync<BootrixException>(() => Provider(web).ListVariantsAsync("manjaro", CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
    }

    [Fact]
    public async Task Products_AreManjaro_AndAnotherIsACallerMistake()
    {
        var provider = Provider(ManjaroSite());

        Assert.Equal("manjaro", Assert.Single(await provider.ListProductsAsync(CancellationToken.None)).Id);
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ListVariantsAsync("arch", CancellationToken.None));
    }
}
