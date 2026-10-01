// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using Bootrix.Core.Catalog;
using Bootrix.Core.Catalog.Distros;
using Bootrix.Core.Errors;
using Bootrix.Core.Net;
using Bootrix.Core.Tests.Catalog.Distros.Support;
using Microsoft.Extensions.Time.Testing;

namespace Bootrix.Core.Tests.Catalog.Distros;

public class ArchLinuxProviderTests
{
    private const string Releases = "https://archlinux.org/releng/releases/json/";
    private const string Mirrors = "https://archlinux.org/mirrors/status/json/";

    private readonly FakeTimeProvider _time = new(DistroFixtures.CapturedOn);

    private static FakeWeb ArchSite() => new FakeWeb()
        .ServeFixture(Releases, "arch/releases.json")
        .ServeFixture(Mirrors, "arch/mirror-status.json");

    private ArchLinuxProvider Provider(FakeWeb web) => new(web.CreateClient(), _time);

    [Fact]
    public void Releases_ListsOnlyTheOnesStillAvailable_NewestFirst()
    {
        var releases = ArchReleases.Parse(DistroFixtures.Text("arch/releases.json"));

        Assert.Equal(["2026.09.01", "2026.08.01", "2026.07.01"], releases.Select(r => r.Version));
        var newest = releases[0];
        Assert.Equal("iso/2026.09.01/archlinux-2026.09.01-x86_64.iso", newest.IsoPath);
        Assert.Equal("archlinux-2026.09.01-x86_64.iso", newest.FileName);
        Assert.Equal("be8458032f8105e60ee2a3067f950b6e3c007ee51b38dac50e8b48e765561c91", newest.Sha256.Hex);
        Assert.Equal(1_608_286_208, newest.Size);
        Assert.Equal(new DateOnly(2026, 9, 1), newest.ReleaseDate);
        Assert.Equal("7.2.2", newest.KernelVersion);
        Assert.Equal("3E80CA1A8B89F69CBA57D98A76A5EF9054449A5C", newest.PgpFingerprint);
    }

    [Fact]
    public void Releases_SkipEntriesWithoutAUsableDigestOrPath()
    {
        const string json = """
            {"releases":[
              {"version":"a","available":true,"iso_url":"/iso/a/a.iso","sha256_sum":"nothex"},
              {"version":"b","available":true,"sha256_sum":"%SHA%"},
              {"version":"c","available":false,"iso_url":"/iso/c/c.iso","sha256_sum":"%SHA%"},
              {"version":"d","available":true,"iso_url":"/iso/d/d.iso","sha256_sum":"%SHA%"}
            ]}
            """;

        var releases = ArchReleases.Parse(json.Replace("%SHA%", new string('a', 64), StringComparison.Ordinal));

        Assert.Equal("d", Assert.Single(releases).Version);
        Assert.Null(releases[0].Size);
    }

    [Fact]
    public void Mirrors_AreHttpsIsoMirrorsThatAreCompleteAndScored_BestFirst()
    {
        var mirrors = ArchReleases.ParseMirrors(DistroFixtures.Text("arch/mirror-status.json"), 20);

        // The fixture also holds an rsync and an http mirror, one without ISOs, one that is only partly synchronised and one without a score.
        Assert.Equal(
            [
                "https://frankfurt.mirror.pkgbuild.com/",
                "https://london.mirror.pkgbuild.com/",
                "https://de.arch.mirror.kescher.at/",
                "https://mirror.osbeck.com/archlinux/",
                "https://us.arch.niranjan.co/",
                "https://at.arch.niranjan.co/",
                "https://umea.mirror.pkgbuild.com/",
                "https://mirror.lcarilla.de/archlinux/",
            ],
            mirrors.Select(m => m.Url.AbsoluteUri));
        Assert.Equal("DE", mirrors[0].Location);
        Assert.Equal("GB", mirrors[1].Location);
        Assert.Equal(Enumerable.Range(2, 8), mirrors.Select(m => m.Priority));
    }

    [Fact]
    public void Mirrors_AreLimited()
    {
        Assert.Equal(3, ArchReleases.ParseMirrors(DistroFixtures.Text("arch/mirror-status.json"), 3).Count);
    }

    [Fact]
    public async Task Variants_CarryTheDigestSizeAndSignatureHintButAreOnlyTlsProtected()
    {
        var variants = await Provider(ArchSite()).ListVariantsAsync("archlinux", CancellationToken.None);

        Assert.Equal(["2026.09.01", "2026.08.01", "2026.07.01"], variants.Select(v => v.Id));
        var newest = variants[0];
        Assert.True(newest.IsRecommended);
        Assert.Equal(1, variants.Count(v => v.IsRecommended));
        Assert.Equal("Arch Linux 2026.09.01", newest.Name);
        Assert.Equal(new DateOnly(2026, 9, 1), newest.ReleaseDate);
        Assert.Equal(1_608_286_208, newest.SizeBytes);
        Assert.Equal(["x64"], newest.Architectures);
        Assert.Equal(DistroProperties.TlsOnly, newest.Properties[DistroProperties.HashTrust]);
        Assert.Equal("iso/2026.09.01/archlinux-2026.09.01-x86_64.iso.sig", newest.Properties["signature"]);
        Assert.Equal("3E80CA1A8B89F69CBA57D98A76A5EF9054449A5C", newest.Properties["pgpFingerprint"]);
    }

    [Fact]
    public async Task Resolve_StartsWithTheGeoRoutedMirrorAndAddsTheBestOthers()
    {
        var provider = Provider(ArchSite());
        var variant = (await provider.ListVariantsAsync("archlinux", CancellationToken.None))[0];

        var request = await provider.ResolveAsync(variant, null, CancellationToken.None);

        Assert.Equal(new Uri("https://geo.mirror.pkgbuild.com/iso/2026.09.01/archlinux-2026.09.01-x86_64.iso"), request.Url);
        Assert.Equal(8, request.Sources.Count);
        Assert.Equal(new Uri("https://frankfurt.mirror.pkgbuild.com/iso/2026.09.01/archlinux-2026.09.01-x86_64.iso"), request.Sources[1].Url);
        Assert.Equal("DE", request.Sources[1].Location);
        Assert.Equal(new Uri("https://mirror.osbeck.com/archlinux/iso/2026.09.01/archlinux-2026.09.01-x86_64.iso"), request.Sources[4].Url);
        Assert.Equal(new FileHash(HashKind.Sha256, "be8458032f8105e60ee2a3067f950b6e3c007ee51b38dac50e8b48e765561c91"), Assert.Single(request.ExpectedHashes));
        Assert.Equal(1_608_286_208, request.ExpectedSize);
    }

    [Fact]
    public async Task Resolve_WithoutTheMirrorStatus_StillDownloadsFromTheGeoMirror()
    {
        var web = ArchSite().Fail(Mirrors, HttpStatusCode.ServiceUnavailable);
        var provider = Provider(web);
        var variant = (await provider.ListVariantsAsync("archlinux", CancellationToken.None))[0];

        var request = await provider.ResolveAsync(variant, "x64", CancellationToken.None);

        Assert.Equal(new Uri("https://geo.mirror.pkgbuild.com/iso/2026.09.01/archlinux-2026.09.01-x86_64.iso"), Assert.Single(request.Sources).Url);
    }

    [Fact]
    public async Task Variants_WhenTheListIsNotJson_ReportCatalogUnavailable()
    {
        var web = new FakeWeb().Serve(Releases, "<html>502</html>");

        var error = await Assert.ThrowsAsync<BootrixException>(() => Provider(web).ListVariantsAsync("archlinux", CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
    }

    [Fact]
    public async Task Resolve_RejectsAVariantThatIsNotFromThisProvider()
    {
        var foreign = new CatalogVariant { Id = "x", ProductId = "archlinux", Provider = "arch", Name = "x" };

        await Assert.ThrowsAsync<ArgumentException>(() => Provider(ArchSite()).ResolveAsync(foreign, null, CancellationToken.None));
    }

    [Fact]
    public async Task Products_AreArch_AndAnUnknownProductIsACallerMistake()
    {
        var provider = Provider(ArchSite());

        Assert.Equal("archlinux", Assert.Single(await provider.ListProductsAsync(CancellationToken.None)).Id);
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ListVariantsAsync("manjaro", CancellationToken.None));
    }
}
