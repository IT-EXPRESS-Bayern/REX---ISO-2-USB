// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Catalog;
using Bootrix.Core.Catalog.Distros;
using Bootrix.Core.Catalog.Distros.Common;
using Bootrix.Core.Errors;
using Bootrix.Core.Net;

namespace Bootrix.Core.Tests.Catalog.Distros;

public class CommonHelpersTests
{
    private static CatalogVariant Variant(params string[] architectures) =>
        new() { Id = "v", ProductId = "p", Provider = "x", Name = "n", Architectures = architectures };

    [Fact]
    public void Architecture_OfASingleChoice_NeedsNoRequest()
    {
        Assert.Equal("x64", Architectures.Select(Variant("x64"), null));
        Assert.Equal("x64", Architectures.Select(Variant("x64"), "X64"));
    }

    [Fact]
    public void Architecture_OfAnArchitectureNeutralVariant_IsNull()
    {
        Assert.Null(Architectures.Select(Variant(), null));
    }

    [Fact]
    public void Architecture_MustBeChosenWhenThereAreSeveral_AndBeOneOfThem()
    {
        var two = Variant("x64", "arm64");

        Assert.Equal("arm64", Architectures.Select(two, "arm64"));
        Assert.Throws<ArgumentException>(() => Architectures.Select(two, null));
        Assert.Throws<ArgumentException>(() => Architectures.Select(two, "i386"));
    }

    [Theory]
    [InlineData("amd64", "x64")]
    [InlineData("x86_64", "x64")]
    [InlineData("64bit", "x64")]
    [InlineData("aarch64", "arm64")]
    [InlineData("ARM64", "arm64")]
    [InlineData("i586", "i386")]
    [InlineData("i686", "i386")]
    [InlineData("ppc64le", null)]
    [InlineData("i686-pae", null)]
    public void VendorArchitectureNames_AreMappedToTheCatalogNames(string vendor, string? expected)
    {
        Assert.Equal(expected, Architectures.FromVendorName(vendor));
    }

    [Fact]
    public void MirrorBuild_PutsThePrimaryFirstAndKeepsCountries()
    {
        var sources = MirrorSources.Build(
            new Uri("https://vendor.example/x/file.iso"),
            [("https://a.example/pub/", "DE"), ("https://b.example/", null)],
            "dir/file.iso");

        Assert.Equal(
            [new MirrorSource(new Uri("https://vendor.example/x/file.iso"), 1), new MirrorSource(new Uri("https://a.example/pub/dir/file.iso"), 2, "DE"), new MirrorSource(new Uri("https://b.example/dir/file.iso"), 2)],
            sources);
    }

    [Fact]
    public void MirrorPick_PrefersHttpsKeepsTheVendorsOrderAndLimits()
    {
        var mirrors = new[]
        {
            new MirrorSource(new Uri("http://a.example/f"), 1),
            new MirrorSource(new Uri("https://c.example/f"), 3),
            new MirrorSource(new Uri("https://b.example/f"), 2),
            new MirrorSource(new Uri("https://d.example/f"), 4),
        };

        Assert.Equal(["b.example", "c.example", "d.example"], MirrorSources.Pick(mirrors).Select(m => m.Url.Host));
        Assert.Equal(["b.example"], MirrorSources.Pick(mirrors, 1).Select(m => m.Url.Host));
    }

    [Fact]
    public void MirrorPick_FallsBackToPlainHttpWhenThatIsAllThereIs()
    {
        var mirrors = new[] { new MirrorSource(new Uri("http://a.example/f"), 1) };

        Assert.Equal("a.example", Assert.Single(MirrorSources.Pick(mirrors)).Url.Host);
    }

    [Fact]
    public void VariantProperties_AlwaysCarryTheTrustLevel()
    {
        var signed = VariantProperties.Signed(("a", "1"));
        var unsigned = VariantProperties.Unsigned(("a", "1"), ("a", "2"));

        Assert.Equal(DistroProperties.PinnedKey, signed[DistroProperties.HashTrust]);
        Assert.Equal(DistroProperties.TlsOnly, unsigned[DistroProperties.HashTrust]);
        Assert.Equal("2", unsigned["a"]);
        Assert.Equal("signed", DistroProperties.PinnedKey);
        Assert.Equal("unsigned", DistroProperties.TlsOnly);
    }

    [Fact]
    public void VariantProperty_OfAForeignVariant_IsACallerMistake()
    {
        var variant = Variant();

        Assert.Throws<ArgumentException>(() => variant.Property("file"));
    }

    [Fact]
    public void Json_ReaderToleratesMissingAndWronglyTypedFields()
    {
        using var document = DistroJson.Parse("""{"s":"x","n":5,"t":"7","bad":true,"nested":{"k":1}}""", "test");
        var root = document.RootElement;

        Assert.Equal("x", root.String("s"));
        Assert.Null(root.String("n"));
        Assert.Null(root.String("missing"));
        Assert.Equal(5, root.Number("n"));
        Assert.Equal(7, root.Number("t"));
        Assert.Null(root.Number("bad"));
        Assert.Equal(1, root.Child("nested")!.Value.Number("k"));
        Assert.Null(root.Child("nothing"));
    }

    [Fact]
    public void Json_ThatIsNotJson_IsACatalogProblem()
    {
        var error = Assert.Throws<BootrixException>(() => DistroJson.Parse("<html>", "the vendor"));

        Assert.Equal(ErrorCode.CatalogUnavailable, error.Code);
        Assert.Contains("the vendor", error.Detail, StringComparison.Ordinal);
    }
}
