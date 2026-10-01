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

public class ClonezillaProviderTests
{
    private const string Mirror = "https://free.nchc.org.tw/clonezilla-live/stable/";

    private readonly FakeTimeProvider _time = new(DistroFixtures.CapturedOn);

    private static FakeWeb ClonezillaSite() => new FakeWeb()
        .ServeFixture(Mirror + "SHA256SUMS", "clonezilla/SHA256SUMS")
        .ServeFixture(Mirror + "SHA256SUMS.gpg", "clonezilla/SHA256SUMS.gpg");

    private ClonezillaProvider Provider(FakeWeb web) => new(web.CreateClient(), _time);

    [Fact]
    public async Task Product_IsARescueTool()
    {
        var product = Assert.Single(await Provider(ClonezillaSite()).ListProductsAsync(CancellationToken.None));

        Assert.Equal("clonezilla", product.Id);
        Assert.Equal(CatalogFamily.Rescue, product.Family);
        Assert.Equal("GPL-2.0", product.License);
    }

    [Fact]
    public async Task Variants_AreTheIsoAndNotTheZip()
    {
        var variants = await Provider(ClonezillaSite()).ListVariantsAsync("clonezilla", CancellationToken.None);

        var only = Assert.Single(variants);
        Assert.Equal("3.3.3-37", only.Id);
        Assert.Equal("Clonezilla Live 3.3.3-37", only.Name);
        Assert.Equal(["x64"], only.Architectures);
        Assert.True(only.IsRecommended);
        Assert.Equal(DistroProperties.PinnedKey, only.Properties[DistroProperties.HashTrust]);
    }

    [Fact]
    public async Task Resolve_VerifiesTheSignatureAndOffersTheProjectMirrorAndSourceForge()
    {
        var provider = Provider(ClonezillaSite());
        var variant = Assert.Single(await provider.ListVariantsAsync("clonezilla", CancellationToken.None));

        var request = await provider.ResolveAsync(variant, null, CancellationToken.None);

        Assert.Equal(
            [
                new Uri(Mirror + "clonezilla-live-3.3.3-37-amd64.iso"),
                new Uri("https://downloads.sourceforge.net/project/clonezilla/clonezilla_live_stable/3.3.3-37/clonezilla-live-3.3.3-37-amd64.iso"),
            ],
            request.Sources.Select(s => s.Url));
        Assert.Equal(new FileHash(HashKind.Sha256, "3079458d926a37d3533e5d5caeb61b6e49c2dc69e2c97e0332ef37986bb3414f"), Assert.Single(request.ExpectedHashes));
    }

    [Fact]
    public async Task Resolve_RejectsAChecksumFileChangedAfterSigning()
    {
        var web = ClonezillaSite().Serve(Mirror + "SHA256SUMS", DistroFixtures.Text("clonezilla/SHA256SUMS").Replace("3079458d", "00000000", StringComparison.Ordinal));
        var provider = Provider(web);
        var variant = Assert.Single(await provider.ListVariantsAsync("clonezilla", CancellationToken.None));

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ResolveAsync(variant, null, CancellationToken.None));

        Assert.Equal(ErrorCode.SignatureInvalid, error.Code);
    }

    [Fact]
    public async Task Resolve_RejectsASignatureMadeByTheGPartedKey()
    {
        // GParted's checksums are signed by another key (a personal key of the same maintainer); it is not accepted here.
        var web = ClonezillaSite()
            .ServeFixture(Mirror + "SHA256SUMS", "gparted/CHECKSUMS.TXT")
            .ServeFixture(Mirror + "SHA256SUMS.gpg", "gparted/CHECKSUMS.TXT.gpg");
        var provider = Provider(web);
        var variant = new CatalogVariant
        {
            Id = "1.8.1-6",
            ProductId = "clonezilla",
            Provider = "clonezilla",
            Name = "x",
            Architectures = ["x64"],
            Properties = VariantProperties.Signed(("version", "1.8.1-6")),
        };

        var error = await Assert.ThrowsAsync<BootrixException>(() => provider.ResolveAsync(variant, null, CancellationToken.None));

        Assert.Equal(ErrorCode.SignatureInvalid, error.Code);
    }

    [Fact]
    public async Task Variants_WhenTheMirrorIsDown_ReportCatalogUnavailable()
    {
        var web = new FakeWeb().Fail(Mirror + "SHA256SUMS", HttpStatusCode.GatewayTimeout);

        var error = await Assert.ThrowsAsync<BootrixException>(() => Provider(web).ListVariantsAsync("clonezilla", CancellationToken.None));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
    }

    [Fact]
    public async Task UnknownProduct_IsACallerMistake()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Provider(ClonezillaSite()).ListVariantsAsync("gparted-live", CancellationToken.None));
    }
}
