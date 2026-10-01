// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using Bootrix.Core.Catalog;
using Bootrix.Core.Catalog.Distros;
using Bootrix.Core.Errors;
using Bootrix.Core.Tests.Catalog.Distros.Support;
using Microsoft.Extensions.Time.Testing;

namespace Bootrix.Core.Tests.Catalog.Distros;

public class FedoraProviderTests
{
    private const string Json = "https://fedoraproject.org/releases.json";
    private const string Dl = "https://dl.fedoraproject.org/pub/fedora/linux/releases/";
    private const string WorkstationIso = "Fedora-Workstation-Live-44-1.7.x86_64.iso";

    private static readonly string MetalinkUrl =
        "https://mirrors.fedoraproject.org/metalink?path=pub/fedora/linux/releases/44/Workstation/x86_64/iso/" + WorkstationIso;

    private readonly FakeTimeProvider _time = new(DistroFixtures.CapturedOn);

    private static FakeWeb FedoraSite() => new FakeWeb()
        .ServeFixture(Json, "fedora/releases.json")
        .ServeFixture(Dl + "44/Workstation/x86_64/iso/Fedora-Workstation-44-1.7-x86_64-CHECKSUM", "fedora/Fedora-Workstation-44-1.7-x86_64-CHECKSUM")
        .ServeFixture(Dl + "44/Workstation/aarch64/iso/Fedora-Workstation-44-1.7-aarch64-CHECKSUM", "fedora/Fedora-Workstation-44-1.7-aarch64-CHECKSUM")
        .ServeFixture(Dl + "44/Server/x86_64/iso/Fedora-Server-44-1.7-x86_64-CHECKSUM", "fedora/Fedora-Server-44-1.7-x86_64-CHECKSUM")
        .ServeFixture(Dl + "44/Spins/x86_64/iso/Fedora-Spins-44-1.7-x86_64-CHECKSUM", "fedora/Fedora-Spins-44-1.7-x86_64-CHECKSUM")
        .ServeFixture(Dl + "44/Everything/x86_64/iso/Fedora-Everything-44-1.7-x86_64-CHECKSUM", "fedora/Fedora-Everything-44-1.7-x86_64-CHECKSUM")
        .ServeFixture(MetalinkUrl, "fedora/Fedora-Workstation-Live-44-1.7.x86_64.iso.metalink");

    private FedoraProvider Provider(FakeWeb web) => new(web.CreateClient(), _time);

    private async Task<CatalogVariant> Variant(FedoraProvider provider, string product, string id) =>
        (await provider.ListVariantsAsync(product, CancellationToken.None)).First(v => v.Id == id);

    [Fact]
    public async Task Products_AreTheEditions()
    {
        var products = await Provider(FedoraSite()).ListProductsAsync(CancellationToken.None);

        Assert.Equal(["fedora-workstation", "fedora-kde", "fedora-server", "fedora-spins", "fedora-everything"], products.Select(p => p.Id));
        Assert.All(products, p => Assert.Equal(CatalogFamily.Linux, p.Family));
    }

    [Fact]
    public async Task Workstation_IsOfferedPerReleaseForPcsAndArm()
    {
        var variants = await Provider(FedoraSite()).ListVariantsAsync("fedora-workstation", CancellationToken.None);

        Assert.Equal(["44/Workstation/live", "43/Workstation/live"], variants.Select(v => v.Id));
        var newest = variants[0];
        Assert.Equal("Fedora Workstation 44", newest.Name);
        Assert.Equal("44", newest.Version);
        Assert.Equal(["x64", "arm64"], newest.Architectures);
        Assert.Equal(2_851_612_672, newest.SizeBytes);
        Assert.True(newest.IsRecommended);
        Assert.False(variants[1].IsRecommended);
    }

    [Fact]
    public async Task Server_RecommendsTheDvdAndNamesBothInstallers()
    {
        var variants = await Provider(FedoraSite()).ListVariantsAsync("fedora-server", CancellationToken.None);

        Assert.Equal(["44/Server/dvd", "44/Server/netinst"], variants.Select(v => v.Id));
        Assert.Equal(["Fedora Server 44 (DVD)", "Fedora Server 44 (netinst)"], variants.Select(v => v.Name));
        Assert.Equal(["44/Server/dvd"], variants.Where(v => v.IsRecommended).Select(v => v.Id));
    }

    [Fact]
    public async Task Spins_AreVariantsOfOneProductAndNeverRecommended()
    {
        var variants = await Provider(FedoraSite()).ListVariantsAsync("fedora-spins", CancellationToken.None);

        Assert.Equal(["Fedora Cinnamon 44", "Fedora KDE Plasma Mobile 44", "Fedora MATE 44", "Fedora Sugar on a Stick 44", "Fedora Xfce 44"], variants.Select(v => v.Name));
        Assert.DoesNotContain(variants, v => v.IsRecommended);
    }

    [Fact]
    public async Task Everything_IsTheNetworkInstaller()
    {
        var variant = Assert.Single(await Provider(FedoraSite()).ListVariantsAsync("fedora-everything", CancellationToken.None), v => v.Id.StartsWith("44/", StringComparison.Ordinal));

        Assert.Equal("Fedora Everything 44 (netinst)", variant.Name);
        Assert.True(variant.IsRecommended);
    }

    [Fact]
    public async Task ReleasesWithoutAPinnedKey_AreNotOffered()
    {
        var web = FedoraSite().Serve(Json, DistroFixtures.Text("fedora/releases.json").Replace("\"44\"", "\"41\"", StringComparison.Ordinal));

        var variants = await Provider(web).ListVariantsAsync("fedora-workstation", CancellationToken.None);

        // 41 has no key in Bootrix, so only 43 can be verified.
        Assert.Equal(["43/Workstation/live"], variants.Select(v => v.Id));
    }

    [Fact]
    public async Task Resolve_UsesTheSignedDigestAndTheMetalinkMirrors()
    {
        var provider = Provider(FedoraSite());
        var variant = await Variant(provider, "fedora-workstation", "44/Workstation/live");

        var request = await provider.ResolveAsync(variant, "x64", CancellationToken.None);

        Assert.Equal("1620295f6a00c27c3208f0c00b8ece4eab1ec69b9002152d97488bf26a426ddf", Assert.Single(request.ExpectedHashes).Hex);
        Assert.Equal(2_851_612_672, request.ExpectedSize);
        // 12 mirrors in the list, 6 of them with https; those come first and are all that is used.
        Assert.Equal(6, request.Sources.Count);
        Assert.All(request.Sources, s => Assert.Equal("https", s.Url.Scheme));
        Assert.Equal("US", request.Sources[0].Location);
        Assert.All(request.Sources, s => Assert.EndsWith("/" + WorkstationIso, s.Url.AbsolutePath, StringComparison.Ordinal));
        Assert.All(request.Sources, s => Assert.Equal(1, s.MaxConnections));
    }

    [Fact]
    public async Task Resolve_ForArm64_ReadsTheAarch64Checksum()
    {
        var web = FedoraSite()
            .Fail("https://mirrors.fedoraproject.org/metalink?path=pub/fedora/linux/releases/44/Workstation/aarch64/iso/Fedora-Workstation-Live-44-1.7.aarch64.iso", HttpStatusCode.NotFound);
        var provider = Provider(web);
        var variant = await Variant(provider, "fedora-workstation", "44/Workstation/live");

        var request = await provider.ResolveAsync(variant, "arm64", CancellationToken.None);

        Assert.Equal("162ba3c552a2d241c7c63ec26777af0255ee1b5a135adc0be986ceed999933ef", request.ExpectedHashes[0].Hex);
        Assert.EndsWith("Fedora-Workstation-Live-44-1.7.aarch64.iso", request.Url.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolve_WithoutAMetalink_FallsBackToTheRedirectorWithTheSizeFromTheList()
    {
        var web = FedoraSite().Fail(MetalinkUrl, HttpStatusCode.ServiceUnavailable);
        var provider = Provider(web);
        var variant = await Variant(provider, "fedora-workstation", "44/Workstation/live");

        var request = await provider.ResolveAsync(variant, "x64", CancellationToken.None);

        Assert.Equal(
            new Uri("https://download.fedoraproject.org/pub/fedora/linux/releases/44/Workstation/x86_64/iso/" + WorkstationIso),
            Assert.Single(request.Sources).Url);
        Assert.Equal(2_851_612_672, request.ExpectedSize);
        Assert.Equal("1620295f6a00c27c3208f0c00b8ece4eab1ec69b9002152d97488bf26a426ddf", request.ExpectedHashes[0].Hex);
    }

    [Fact]
    public async Task Resolve_IgnoresAMetalinkWhoseDigestDisagreesWithTheSignedOne()
    {
        var web = FedoraSite().Serve(MetalinkUrl, DistroFixtures.Text("fedora/Fedora-Workstation-Live-44-1.7.x86_64.iso.metalink").Replace("1620295f", "ffffffff", StringComparison.Ordinal));
        var provider = Provider(web);
        var variant = await Variant(provider, "fedora-workstation", "44/Workstation/live");

        var request = await provider.ResolveAsync(variant, "x64", CancellationToken.None);

        Assert.Single(request.Sources);
        Assert.Equal("1620295f6a00c27c3208f0c00b8ece4eab1ec69b9002152d97488bf26a426ddf", request.ExpectedHashes[0].Hex);
    }

    [Fact]
    public async Task Resolve_ServerInstaller_ReadsItsOwnLineOfTheSharedChecksumFile()
    {
        var web = FedoraSite().Fail("https://mirrors.fedoraproject.org/metalink?path=pub/fedora/linux/releases/44/Server/x86_64/iso/Fedora-Server-netinst-x86_64-44-1.7.iso", HttpStatusCode.NotFound);
        var provider = Provider(web);
        var variant = await Variant(provider, "fedora-server", "44/Server/netinst");

        var request = await provider.ResolveAsync(variant, "x64", CancellationToken.None);

        Assert.Equal("Fedora-Server-netinst-x86_64-44-1.7.iso", request.Url.Segments[^1]);
        Assert.Equal(1_228_384_256, request.ExpectedSize);
        Assert.NotEqual("85837793bfa36db6bc709b4cecd2ec116951b87d9c53c3d95eb2fac8dcf7cf1f", request.ExpectedHashes[0].Hex);
    }

    [Fact]
    public async Task Resolve_RejectsAChecksumFileChangedAfterSigning()
    {
        var web = FedoraSite().Serve(
            Dl + "44/Workstation/x86_64/iso/Fedora-Workstation-44-1.7-x86_64-CHECKSUM",
            DistroFixtures.Text("fedora/Fedora-Workstation-44-1.7-x86_64-CHECKSUM").Replace("1620295f", "00000000", StringComparison.Ordinal));
        var provider = Provider(web);
        var variant = await Variant(provider, "fedora-workstation", "44/Workstation/live");

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ResolveAsync(variant, "x64", CancellationToken.None));

        Assert.Equal(ErrorCode.SignatureInvalid, error.Code);
    }

    [Fact]
    public async Task Resolve_RejectsASignatureByAnotherReleasesKey()
    {
        // A perfectly valid Fedora 43 checksum file; it is signed by the 43 key, which may not vouch for release 44.
        var web = FedoraSite().ServeFixture(
            Dl + "44/Workstation/x86_64/iso/Fedora-Workstation-44-1.7-x86_64-CHECKSUM",
            "fedora/Fedora-Workstation-43-1.6-x86_64-CHECKSUM");
        var provider = Provider(web);
        var variant = await Variant(provider, "fedora-workstation", "44/Workstation/live");

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ResolveAsync(variant, "x64", CancellationToken.None));

        Assert.Equal(ErrorCode.SignatureInvalid, error.Code);
    }

    [Fact]
    public async Task Resolve_RejectsAnUnsignedChecksumFile()
    {
        var web = FedoraSite().Serve(
            Dl + "44/Workstation/x86_64/iso/Fedora-Workstation-44-1.7-x86_64-CHECKSUM",
            "SHA256 (" + WorkstationIso + ") = " + new string('a', 64) + "\n");
        var provider = Provider(web);
        var variant = await Variant(provider, "fedora-workstation", "44/Workstation/live");

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ResolveAsync(variant, "x64", CancellationToken.None));

        Assert.Equal(ErrorCode.SignatureInvalid, error.Code);
    }

    [Fact]
    public async Task Resolve_WhenTheSignedFileDoesNotListTheImage_ReportsTheMissingEntry()
    {
        var web = FedoraSite().ServeFixture(
            Dl + "44/Workstation/x86_64/iso/Fedora-Workstation-44-1.7-x86_64-CHECKSUM",
            "fedora/Fedora-KDE-44-1.7-x86_64-CHECKSUM");
        var provider = Provider(web);
        var variant = await Variant(provider, "fedora-workstation", "44/Workstation/live");

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ResolveAsync(variant, "x64", CancellationToken.None));

        Assert.Equal(ErrorCode.ChecksumFileInvalid, error.Code);
    }

    [Fact]
    public async Task Resolve_NeedsAnArchitectureWhenThereAreTwo()
    {
        var provider = Provider(FedoraSite());
        var variant = await Variant(provider, "fedora-workstation", "44/Workstation/live");

        await Assert.ThrowsAsync<ArgumentException>(() => provider.ResolveAsync(variant, null, CancellationToken.None));
    }

    [Fact]
    public async Task Variants_WhenReleasesJsonIsDown_ReportCatalogUnavailable()
    {
        var web = new FakeWeb().Fail(Json, HttpStatusCode.InternalServerError);

        var error = await Assert.ThrowsAsync<BootrixException>(() => Provider(web).ListVariantsAsync("fedora-workstation", CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
    }
}
