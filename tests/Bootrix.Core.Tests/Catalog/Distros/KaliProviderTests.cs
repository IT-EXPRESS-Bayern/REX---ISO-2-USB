// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using Bootrix.Core.Catalog;
using Bootrix.Core.Catalog.Distros;
using Bootrix.Core.Catalog.Distros.Common;
using Bootrix.Core.Errors;
using Bootrix.Core.Tests.Catalog.Distros.Support;
using Microsoft.Extensions.Time.Testing;

namespace Bootrix.Core.Tests.Catalog.Distros;

public class KaliProviderTests
{
    private const string Base = "https://cdimage.kali.org/current/";

    private readonly FakeTimeProvider _time = new(DistroFixtures.CapturedOn);

    private static FakeWeb KaliSite() => new FakeWeb()
        .ServeFixture(Base, "kali/current-index.html")
        .ServeFixture(Base + "SHA256SUMS", "kali/SHA256SUMS")
        .ServeFixture(Base + "SHA256SUMS.gpg", "kali/SHA256SUMS.gpg");

    private KaliProvider Provider(FakeWeb web) => new(web.CreateClient(), _time);

    [Fact]
    public void Images_AreTheIsosOnTheServer_NotTheOnesOnlyTheChecksumFileKnows()
    {
        var images = KaliImages.Parse(DirectoryListing.Parse(DistroFixtures.Text("kali/current-index.html")));

        // Live and "everything" images are listed in SHA256SUMS but are distributed by torrent only; virtual machine images are not boot media.
        Assert.Equal(
            ["installer", "installer-netinst", "installer-purple"],
            images.Select(i => i.Kind));
        Assert.All(images, i => Assert.Equal("2026.2", i.Version));
        Assert.Equal(["x64", "arm64"], images[0].Architectures);
        Assert.Equal(["x64", "arm64"], images[1].Architectures);
        Assert.Equal(["x64"], images[2].Architectures);
    }

    [Fact]
    public async Task Variants_NameTheKindAndRecommendTheInstaller()
    {
        var variants = await Provider(KaliSite()).ListVariantsAsync("kali", CancellationToken.None);

        Assert.Equal(["2026.2/installer", "2026.2/installer-netinst", "2026.2/installer-purple"], variants.Select(v => v.Id));
        Assert.Equal(
            ["Kali Linux 2026.2 Installer", "Kali Linux 2026.2 Netinst (network installer)", "Kali Linux 2026.2 Purple (defensive tools)"],
            variants.Select(v => v.Name));
        Assert.Equal(["2026.2/installer"], variants.Where(v => v.IsRecommended).Select(v => v.Id));
        Assert.All(variants, v => Assert.Equal(DistroProperties.PinnedKey, v.Properties[DistroProperties.HashTrust]));
    }

    [Fact]
    public async Task Resolve_VerifiesTheSignatureAndUsesTheDigestOfTheChosenArchitecture()
    {
        var provider = Provider(KaliSite());
        var variant = (await provider.ListVariantsAsync("kali", CancellationToken.None))[1];

        var x64 = await provider.ResolveAsync(variant, "x64", CancellationToken.None);
        var arm = await provider.ResolveAsync(variant, "arm64", CancellationToken.None);

        Assert.Equal(new Uri(Base + "kali-linux-2026.2-installer-netinst-amd64.iso"), x64.Url);
        Assert.Equal("d32f929dacc48134a31461a09f2160d13ad1d26b820cee920446813ca979b39b", x64.ExpectedHashes[0].Hex);
        Assert.Equal(new Uri(Base + "kali-linux-2026.2-installer-netinst-arm64.iso"), arm.Url);
        Assert.Equal("8ca1bdd6a85f37e380eca84c780256b6e634fc768426396f51967ec3be006ed1", arm.ExpectedHashes[0].Hex);
    }

    [Fact]
    public async Task Resolve_RejectsAChecksumFileChangedAfterSigning()
    {
        var web = KaliSite().Serve(Base + "SHA256SUMS", DistroFixtures.Text("kali/SHA256SUMS").Replace("d32f929d", "00000000", StringComparison.Ordinal));
        var provider = Provider(web);
        var variant = (await provider.ListVariantsAsync("kali", CancellationToken.None))[1];

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ResolveAsync(variant, "x64", CancellationToken.None));

        Assert.Equal(ErrorCode.SignatureInvalid, error.Code);
    }

    [Fact]
    public async Task Resolve_RejectsASignatureByUbuntusKey()
    {
        var web = KaliSite().ServeFixture(Base + "SHA256SUMS.gpg", "ubuntu/ubuntu-26.04.SHA256SUMS.gpg");
        var provider = Provider(web);
        var variant = (await provider.ListVariantsAsync("kali", CancellationToken.None))[0];

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ResolveAsync(variant, "x64", CancellationToken.None));

        Assert.Equal(ErrorCode.SignatureInvalid, error.Code);
    }

    [Fact]
    public async Task Resolve_ForAnArchitectureTheImageDoesNotComeIn_IsACallerMistake()
    {
        var provider = Provider(KaliSite());
        var purple = (await provider.ListVariantsAsync("kali", CancellationToken.None))[2];

        await Assert.ThrowsAsync<ArgumentException>(() => provider.ResolveAsync(purple, "arm64", CancellationToken.None));
    }

    [Fact]
    public async Task Resolve_WhenTheSignedFileDoesNotListTheImage_ReportsTheMissingEntry()
    {
        // A variant from an earlier listing: the release has moved on and the signed file no longer names it.
        var stale = new CatalogVariant
        {
            Id = "2024.9/installer",
            ProductId = "kali",
            Provider = "kali",
            Name = "Kali Linux 2024.9 Installer",
            Architectures = ["x64"],
            Properties = VariantProperties.Signed(("version", "2024.9"), ("kind", "installer")),
        };

        var error = await Assert.ThrowsAsync<BootrixException>(() => Provider(KaliSite()).ResolveAsync(stale, "x64", CancellationToken.None));

        Assert.Equal(ErrorCode.ChecksumFileInvalid, error.Code);
    }

    [Fact]
    public async Task Variants_WhenTheMirrorPageIsDown_ReportCatalogUnavailable()
    {
        var web = new FakeWeb().Fail(Base, HttpStatusCode.ServiceUnavailable);

        var error = await Assert.ThrowsAsync<BootrixException>(() => Provider(web).ListVariantsAsync("kali", CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
    }

    [Fact]
    public async Task Products_AreKali_AndAnotherIsACallerMistake()
    {
        var provider = Provider(KaliSite());

        Assert.Equal("kali", Assert.Single(await provider.ListProductsAsync(CancellationToken.None)).Id);
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ListVariantsAsync("parrot", CancellationToken.None));
    }
}
