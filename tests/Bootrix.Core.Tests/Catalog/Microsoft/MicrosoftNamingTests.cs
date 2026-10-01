// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Catalog;
using Bootrix.Core.Catalog.Microsoft;

namespace Bootrix.Core.Tests.Catalog.Microsoft;

public class MicrosoftNamingTests
{
    [Theory]
    [InlineData("sr-latn-rs", "sr-Latn-RS")]
    [InlineData("de-de", "de-DE")]
    [InlineData("ZH-cn", "zh-CN")]
    [InlineData("en", "en")]
    [InlineData(" nb-no ", "nb-NO")]
    [InlineData("es-419", "es-419")]
    public void Normalize_BringsLanguageTagsIntoTheUsualCasing(string written, string expected)
    {
        Assert.Equal(expected, LanguageTags.Normalize(written));
    }

    [Theory]
    [InlineData("German", "de-DE")]
    [InlineData("german", "de-DE")]
    [InlineData("English International", "en-GB")]
    [InlineData("English (United Kingdom)", "en-GB")]
    [InlineData("Chinese Simplified", "zh-CN")]
    [InlineData("Norwegian", "nb-NO")]
    public void FromName_KnownNames_GiveTheirTags(string name, string expected)
    {
        Assert.Equal(expected, LanguageTags.FromName(name));
    }

    [Fact]
    public void FromName_UnknownName_IsNull()
    {
        Assert.Null(LanguageTags.FromName("Elvish"));
    }

    [Fact]
    public void FromName_EveryLanguageOfTheRealApiLists_HasATag()
    {
        foreach (var fixture in new[] { "skus-3813.json", "skus-3816.json", "skus-2618.json" })
        {
            foreach (var sku in MicrosoftApiResponses.ParseSkus(MicrosoftFixtures.Text(fixture)).Skus)
            {
                Assert.True(
                    LanguageTags.FromName(sku.Language) is not null || LanguageTags.FromName(sku.LocalizedLanguage) is not null,
                    $"{sku.Language} has no tag");
            }
        }
    }

    [Theory]
    [InlineData("24H2", "26100.4349", "24H2 (26100.4349)")]
    [InlineData("22h2", null, "22H2")]
    [InlineData(null, "26300.9457", "26H2 (26300.9457)")]
    [InlineData(null, "19045.3803", "22H2 (19045.3803)")]
    [InlineData(null, "12345.1", "12345.1")]
    [InlineData(null, null, null)]
    public void Describe_CombinesReleaseNameAndBuild(string? release, string? build, string? expected)
    {
        Assert.Equal(expected, WindowsReleases.Describe(release, build));
    }

    [Theory]
    [InlineData("Windows 10 22H2_v1", "22H2")]
    [InlineData("Windows 11 ARM 64 26H2 26300.9457", "26H2")]
    [InlineData("Windows 11 Client - Build 26300.9457", null)]
    [InlineData("Windows 11 X122H2Y", null)]
    public void FindRelease_FindsOnlyStandaloneReleaseNames(string text, string? expected)
    {
        Assert.Equal(expected, WindowsReleases.FindRelease(text));
    }

    [Theory]
    [InlineData("Windows 11 Client - Build 26300.9457", "26300.9457")]
    [InlineData("Windows 11 ARM 64 26H2 26300.9457", "26300.9457")]
    [InlineData("Windows 10 22H2_v1", null)]
    [InlineData("Version 1.2.3.4", null)]
    public void FindBuild_FindsTheFiveDigitBuildWithItsRevision(string text, string? expected)
    {
        Assert.Equal(expected, WindowsReleases.FindBuild(text));
    }

    private static CatalogVariant Variant(params string[] architectures) => new()
    {
        Id = "x",
        ProductId = "p",
        Provider = "microsoft",
        Name = "Test",
        Architectures = architectures,
    };

    [Theory]
    [InlineData(null, "x64")]
    [InlineData("arm64", "arm64")]
    [InlineData("ARM64", "arm64")]
    [InlineData("x86", "x86")]
    public void Choose_WithSeveralArchitectures_HonoursTheRequestAndDefaultsToSixtyFourBit(string? requested, string expected)
    {
        Assert.Equal(expected, ArchitectureChoice.Choose(Variant("x86", "arm64", "x64"), requested));
    }

    [Fact]
    public void Choose_WithoutX64AndWithoutRequest_TakesTheFirst()
    {
        Assert.Equal("arm64", ArchitectureChoice.Choose(Variant("arm64", "x86"), null));
    }

    [Fact]
    public void Choose_NoArchitecturesAtAll_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => ArchitectureChoice.Choose(Variant(), null));
        Assert.Throws<ArgumentException>(() => ArchitectureChoice.Choose(Variant("x64"), "arm64"));
    }

    [Fact]
    public void Sort_PutsSixtyFourBitFirstThenArmThenThirtyTwoBit()
    {
        Assert.Equal(["x64", "arm64", "x86", "riscv"], ArchitectureChoice.Sort(["x86", "riscv", "arm64", "x64", "x64"]));
    }
}
