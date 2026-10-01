// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using Bootrix.Core.Catalog;
using Bootrix.Core.Catalog.Distros;
using Bootrix.Core.Errors;
using Bootrix.Core.Net;
using Bootrix.Core.Tests.Catalog.Distros.Support;
using Microsoft.Extensions.Time.Testing;

namespace Bootrix.Core.Tests.Catalog.Distros;

public class OpenSuseProviderTests
{
    private const string Api = "https://get.opensuse.org/api/v0/distributions.json";
    private const string Tumbleweed = "https://download.opensuse.org/tumbleweed/iso/openSUSE-Tumbleweed-NET-x86_64-Current.iso";
    private const string Leap15 = "https://download.opensuse.org/distribution/leap/15.6/iso/openSUSE-Leap-15.6-NET-x86_64-Media.iso";
    private const string Leap16 = "https://download.opensuse.org/distribution/leap/16.0/offline/Leap-16.0-online-installer-x86_64.install.iso";

    private readonly FakeTimeProvider _time = new(DistroFixtures.CapturedOn);

    private static FakeWeb SuseSite() => new FakeWeb()
        .ServeFixture(Api, "opensuse/distributions.json")
        .ServeFixture(Tumbleweed + ".sha256", "opensuse/Tumbleweed-NET.sha256")
        .ServeFixture(Tumbleweed + ".sha256.asc", "opensuse/Tumbleweed-NET.sha256.asc")
        .ServeFixture(Tumbleweed + ".meta4", "opensuse/Tumbleweed-NET.meta4")
        .ServeFixture(Leap15 + ".sha256", "opensuse/Leap-15.6-NET.sha256")
        .ServeFixture(Leap15 + ".sha256.asc", "opensuse/Leap-15.6-NET.sha256.asc")
        .ServeFixture(Leap15 + ".meta4", "opensuse/Leap-15.6-NET.meta4")
        .ServeFixture(Leap16 + ".sha512", "opensuse/Leap-16.0-online.sha512")
        .ServeFixture(Leap16 + ".sha512.asc", "opensuse/Leap-16.0-online.sha512.asc")
        .ServeFixture(Leap16 + ".meta4", "opensuse/Leap-16.0-online.meta4");

    private OpenSuseProvider Provider(FakeWeb web) => new(web.CreateClient(), _time);

    private async Task<CatalogVariant> Variant(OpenSuseProvider provider, string product, string id) =>
        (await provider.ListVariantsAsync(product, CancellationToken.None)).First(v => v.Id == id);

    [Fact]
    public void StableLeap_SkipsReleaseCandidatesAndEndOfLifeReleases()
    {
        var versions = OpenSuseReleases.StableLeap(DistroFixtures.Text("opensuse/distributions.json"));

        // The list also has 16.1 (RC) and 15.5 and older (EOL).
        Assert.Equal(["16.0", "15.6"], versions.Select(v => v.ToString()));
    }

    [Fact]
    public async Task Leap_OffersTheInstallersOfEveryStableRelease()
    {
        var variants = await Provider(SuseSite()).ListVariantsAsync("opensuse-leap", CancellationToken.None);

        Assert.Equal(["16.0/offline", "16.0/online", "15.6/dvd", "15.6/net"], variants.Select(v => v.Id));
        Assert.Equal(
            ["openSUSE Leap 16.0 (offline installer)", "openSUSE Leap 16.0 (online installer)", "openSUSE Leap 15.6 (DVD)", "openSUSE Leap 15.6 (network installer)"],
            variants.Select(v => v.Name));
        Assert.Equal(["16.0", "16.0", "15.6", "15.6"], variants.Select(v => v.Version));
        Assert.Equal(["16.0/offline"], variants.Where(v => v.IsRecommended).Select(v => v.Id));
        Assert.All(variants, v => Assert.Equal(["x64", "arm64"], v.Architectures));
    }

    [Fact]
    public async Task Tumbleweed_IsTheRollingDvdNetworkAndLiveImages()
    {
        var variants = await Provider(SuseSite()).ListVariantsAsync("opensuse-tumbleweed", CancellationToken.None);

        Assert.Equal(
            ["tumbleweed/dvd", "tumbleweed/net", "tumbleweed/kde-live", "tumbleweed/gnome-live", "tumbleweed/xfce-live"],
            variants.Select(v => v.Id));
        Assert.Null(variants[0].Version);
        Assert.Equal(["tumbleweed/dvd"], variants.Where(v => v.IsRecommended).Select(v => v.Id));
        Assert.All(variants, v => Assert.Equal(["x64"], v.Architectures));
        Assert.All(variants, v => Assert.Equal(DistroProperties.PinnedKey, v.Properties[DistroProperties.HashTrust]));
    }

    [Fact]
    public async Task Tumbleweed_NeedsNoNetworkToBeListed()
    {
        var web = new FakeWeb();

        await Provider(web).ListVariantsAsync("opensuse-tumbleweed", CancellationToken.None);

        Assert.Empty(web.Requests);
    }

    [Fact]
    public async Task Resolve_Tumbleweed_UsesTheSnapshotDigestAndTheMetalinkWithPieces()
    {
        var provider = Provider(SuseSite());
        var variant = await Variant(provider, "opensuse-tumbleweed", "tumbleweed/net");

        var request = await provider.ResolveAsync(variant, null, CancellationToken.None);

        var hash = Assert.Single(request.ExpectedHashes);
        Assert.Equal(new FileHash(HashKind.Sha256, "48436b02921bb1e3f4c385db8861baeee2368d2c60b490bc17373f1ba374c586"), hash);
        Assert.Equal(419_430_400, request.ExpectedSize);
        Assert.NotEmpty(request.Pieces);
        Assert.Equal(419_430_400, request.Pieces.Sum(p => p.Length));
        Assert.InRange(request.Sources.Count, 2, 8);
        Assert.All(request.Sources, s => Assert.Equal("https", s.Url.Scheme));
        Assert.All(request.Sources, s => Assert.EndsWith("openSUSE-Tumbleweed-NET-x86_64-Snapshot20260929-Media.iso", s.Url.AbsolutePath, StringComparison.Ordinal));
        Assert.Equal(request.Sources.Select(s => s.Priority).Order(), request.Sources.Select(s => s.Priority));
    }

    [Fact]
    public async Task Resolve_Leap15_ReadsTheSingleLineChecksumFile()
    {
        var provider = Provider(SuseSite());
        var variant = await Variant(provider, "opensuse-leap", "15.6/net");

        var request = await provider.ResolveAsync(variant, "x64", CancellationToken.None);

        Assert.Equal("0984b36d0f420487f2766733ff8f9d779ade81d432b08e40e852a675313750bb", request.ExpectedHashes[0].Hex);
        Assert.Equal(HashKind.Sha256, request.ExpectedHashes[0].Kind);
        Assert.Equal(273_678_336, request.ExpectedSize);
        Assert.Equal("US", request.Sources[0].Location);
    }

    [Fact]
    public async Task Resolve_Leap16_UsesTheSha512Checksum()
    {
        var provider = Provider(SuseSite());
        var variant = await Variant(provider, "opensuse-leap", "16.0/online");

        var request = await provider.ResolveAsync(variant, "x64", CancellationToken.None);

        var hash = Assert.Single(request.ExpectedHashes);
        Assert.Equal(HashKind.Sha512, hash.Kind);
        Assert.StartsWith("47241c70", hash.Hex, StringComparison.Ordinal);
        Assert.Equal(746_586_112, request.ExpectedSize);
    }

    [Fact]
    public async Task Resolve_Arm64_AsksForTheAarch64Files()
    {
        var web = SuseSite();
        var arm = Leap15.Replace("x86_64", "aarch64", StringComparison.Ordinal);
        web.ServeFixture(arm + ".sha256", "opensuse/Leap-15.6-NET.sha256")
            .ServeFixture(arm + ".sha256.asc", "opensuse/Leap-15.6-NET.sha256.asc")
            .Fail(arm + ".meta4", HttpStatusCode.NotFound);
        var provider = Provider(web);
        var variant = await Variant(provider, "opensuse-leap", "15.6/net");

        var request = await provider.ResolveAsync(variant, "arm64", CancellationToken.None);

        Assert.Equal(new Uri(arm), Assert.Single(request.Sources).Url);
    }

    [Fact]
    public async Task Resolve_WithoutAMetalink_FallsBackToTheRedirectorAndKeepsTheSignedDigest()
    {
        var web = SuseSite().Fail(Leap15 + ".meta4", HttpStatusCode.NotFound);
        var provider = Provider(web);
        var variant = await Variant(provider, "opensuse-leap", "15.6/net");

        var request = await provider.ResolveAsync(variant, "x64", CancellationToken.None);

        Assert.Equal(new Uri(Leap15), Assert.Single(request.Sources).Url);
        Assert.Equal("0984b36d0f420487f2766733ff8f9d779ade81d432b08e40e852a675313750bb", request.ExpectedHashes[0].Hex);
        Assert.Null(request.ExpectedSize);
        Assert.Empty(request.Pieces);
    }

    [Fact]
    public async Task Resolve_IgnoresAMetalinkOfADifferentBuild()
    {
        var web = SuseSite().ServeFixture(Leap15 + ".meta4", "opensuse/Tumbleweed-NET.meta4");
        var provider = Provider(web);
        var variant = await Variant(provider, "opensuse-leap", "15.6/net");

        var request = await provider.ResolveAsync(variant, "x64", CancellationToken.None);

        Assert.Equal(new Uri(Leap15), Assert.Single(request.Sources).Url);
    }

    [Fact]
    public async Task Resolve_IgnoresABrokenMetalink()
    {
        var web = SuseSite().Serve(Leap15 + ".meta4", "<html>not a metalink</html>");
        var provider = Provider(web);
        var variant = await Variant(provider, "opensuse-leap", "15.6/net");

        var request = await provider.ResolveAsync(variant, "x64", CancellationToken.None);

        Assert.Single(request.Sources);
    }

    [Fact]
    public async Task Resolve_RejectsAChecksumFileThatWasChangedAfterSigning()
    {
        var web = SuseSite().Serve(Leap15 + ".sha256", DistroFixtures.Text("opensuse/Leap-15.6-NET.sha256").Replace("0984b36d", "00000000", StringComparison.Ordinal));
        var provider = Provider(web);
        var variant = await Variant(provider, "opensuse-leap", "15.6/net");

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ResolveAsync(variant, "x64", CancellationToken.None));

        Assert.Equal(ErrorCode.SignatureInvalid, error.Code);
    }

    [Fact]
    public async Task Resolve_RejectsASignatureForAnotherImage()
    {
        var web = SuseSite().ServeFixture(Leap15 + ".sha256.asc", "opensuse/Tumbleweed-NET.sha256.asc");
        var provider = Provider(web);
        var variant = await Variant(provider, "opensuse-leap", "15.6/net");

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ResolveAsync(variant, "x64", CancellationToken.None));

        Assert.Equal(ErrorCode.SignatureInvalid, error.Code);
    }

    [Fact]
    public async Task Resolve_WithoutASignatureFile_Fails()
    {
        var web = SuseSite().Fail(Leap15 + ".sha256.asc", HttpStatusCode.NotFound);
        var provider = Provider(web);
        var variant = await Variant(provider, "opensuse-leap", "15.6/net");

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ResolveAsync(variant, "x64", CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
    }

    [Fact]
    public async Task Leap_WhenTheReleaseListIsDown_ReportsCatalogUnavailable()
    {
        var web = new FakeWeb().Fail(Api, HttpStatusCode.BadGateway);

        var error = await Assert.ThrowsAsync<BootrixException>(() => Provider(web).ListVariantsAsync("opensuse-leap", CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
    }

    [Fact]
    public async Task Products_AreLeapAndTumbleweed_AndAnotherIsACallerMistake()
    {
        var provider = Provider(SuseSite());

        var products = await provider.ListProductsAsync(CancellationToken.None);

        Assert.Equal(["opensuse-leap", "opensuse-tumbleweed"], products.Select(p => p.Id));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ListVariantsAsync("sles", CancellationToken.None));
    }
}
