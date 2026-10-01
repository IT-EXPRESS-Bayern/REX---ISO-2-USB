// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Bootrix.Core.Catalog.Microsoft;
using Bootrix.Core.Net;

namespace Bootrix.Core.Tests.Catalog.Microsoft;

public class ProductsCatalogTests
{
    private const string GoodFile = """
        <File id="">
          <FileName>26100.4349.250607-1500.ge_release_svc_refresh_CLIENTCONSUMER_RET_x64FRE_de-de.esd</FileName>
          <LanguageCode>de-de</LanguageCode>
          <Language>German (Germany)</Language>
          <Edition>{0}</Edition>
          <Architecture>x64</Architecture>
          <Size>4685348140</Size>
          <Sha1>f1f75016fdee2d81d220be4ac2876f6f564e780f</Sha1>
          <FilePath>http://dl.delivery.mp.microsoft.com/filestreamingservice/files/1094408c-5ad0-4d3c-aaa0-db1e353ec0d2/26100.4349.250607-1500.ge_release_svc_refresh_CLIENTCONSUMER_RET_x64FRE_de-de.esd</FilePath>
          <Key />
          <IsRetailOnly>False</IsRetailOnly>
        </File>
        """;

    private static byte[] Catalog(params string[] files) =>
        Encoding.UTF8.GetBytes($"<MCT><Catalogs><Catalog><PublishedMedia><Files>{string.Concat(files)}</Files></PublishedMedia></Catalog></Catalogs></MCT>");

    private static byte[] RealCatalog(string fixture)
    {
        var cabinet = CabinetArchive.Parse(MicrosoftFixtures.Bytes(fixture));
        return cabinet.Extract(Assert.Single(cabinet.Entries));
    }

    [Fact]
    public void Parse_RealWindows11Catalog_FoldsEditionsIntoOneImagePerFile()
    {
        var images = ProductsCatalog.Parse(RealCatalog("products-win11-24h2.cab"));

        // 38 languages in two media families and two architectures, plus the single Chinese-market file.
        Assert.Equal(153, images.Count);
        Assert.Equal(images.Count, images.Select(i => i.FileName).Distinct().Count());

        var german = Assert.Single(images, i => i.FileName.EndsWith("CLIENTCONSUMER_RET_x64FRE_de-de.esd", StringComparison.Ordinal));
        Assert.Equal("de-DE", german.LanguageCode);
        Assert.Equal("German (Germany)", german.Language);
        Assert.Equal("x64", german.Architecture);
        Assert.Equal(4_685_348_140, german.Size);
        Assert.Equal(new FileHash(HashKind.Sha1, "f1f75016fdee2d81d220be4ac2876f6f564e780f"), german.Sha1);
        Assert.Equal(EsdChannel.Consumer, german.Channel);
        Assert.Equal("26100.4349", german.Build);
        Assert.Equal(new DateOnly(2025, 6, 7), german.Built);
        Assert.Contains("Core", german.Editions);
        Assert.Contains("Professional", german.Editions);
        Assert.Equal(german.Editions.Count, german.Editions.Distinct().Count());
    }

    [Fact]
    public void Parse_RealWindows10Catalog_KeepsThreeArchitecturesAndTheReleaseName()
    {
        var images = ProductsCatalog.Parse(RealCatalog("products-win10-22h2.cab"));

        Assert.Equal(230, images.Count);
        Assert.Equal(["arm64", "x64", "x86"], images.Select(i => i.Architecture).Distinct().Order());

        var business = Assert.Single(images, i => i.FileName.EndsWith("CLIENTBUSINESS_VOL_x86FRE_de-de.esd", StringComparison.Ordinal));
        Assert.Equal(EsdChannel.Business, business.Channel);
        Assert.Equal("22H2", business.Release);
        Assert.Equal("19045.3803", business.Build);
        Assert.Equal(["Enterprise", "EnterpriseN"], business.Editions);
    }

    [Fact]
    public void Parse_ChineseMarketFiles_GetTheirOwnChannel()
    {
        var images = ProductsCatalog.Parse(RealCatalog("products-win11-24h2.cab"));

        var china = Assert.Single(images, i => i.Channel == EsdChannel.China);

        Assert.Equal("zh-CN", china.LanguageCode);
        Assert.Equal(["CoreCountrySpecific", "CoreConnectedCountrySpecific"], china.Editions);
    }

    [Theory]
    [InlineData("AMD64", "x64")]
    [InlineData("i386", "x86")]
    [InlineData("ARM64", "arm64")]
    [InlineData("x86", "x86")]
    public void Parse_ArchitectureNames_AreNormalized(string written, string expected)
    {
        var xml = Catalog(GoodFile.Replace("<Architecture>x64</Architecture>", $"<Architecture>{written}</Architecture>", StringComparison.Ordinal).Replace("{0}", "Core", StringComparison.Ordinal));

        Assert.Equal(expected, Assert.Single(ProductsCatalog.Parse(xml)).Architecture);
    }

    [Fact]
    public void Parse_EntriesWithBrokenFields_AreSkippedAndTheRestIsKept()
    {
        var good = GoodFile.Replace("{0}", "Core", StringComparison.Ordinal);
        var shortDigest = good.Replace("f1f75016fdee2d81d220be4ac2876f6f564e780f", "f1f75016", StringComparison.Ordinal)
            .Replace("x64FRE_de-de.esd</FileName>", "x64FRE_xx-xx.esd</FileName>", StringComparison.Ordinal);
        var noSize = good.Replace("<Size>4685348140</Size>", "<Size></Size>", StringComparison.Ordinal)
            .Replace("x64FRE_de-de.esd</FileName>", "x64FRE_yy-yy.esd</FileName>", StringComparison.Ordinal);
        var relative = good.Replace("http://dl.delivery.mp.microsoft.com", "/files", StringComparison.Ordinal)
            .Replace("x64FRE_de-de.esd</FileName>", "x64FRE_zz-zz.esd</FileName>", StringComparison.Ordinal);
        var ftp = good.Replace("http://", "ftp://", StringComparison.Ordinal)
            .Replace("x64FRE_de-de.esd</FileName>", "x64FRE_ww-ww.esd</FileName>", StringComparison.Ordinal);

        var images = ProductsCatalog.Parse(Catalog(shortDigest, good, noSize, relative, ftp));

        Assert.Single(images);
    }

    [Fact]
    public void Parse_CatalogWithoutUsableImage_IsRejected()
    {
        Assert.Throws<InvalidDataException>(() => ProductsCatalog.Parse(Catalog()));
        Assert.Throws<InvalidDataException>(() => ProductsCatalog.Parse(Catalog("<File><FileName>x.esd</FileName></File>")));
    }

    [Fact]
    public void Parse_EditionsOfTheSameFile_AreCollectedOnce()
    {
        var xml = Catalog(
            GoodFile.Replace("{0}", "Core", StringComparison.Ordinal),
            GoodFile.Replace("{0}", "Professional", StringComparison.Ordinal),
            GoodFile.Replace("{0}", "Core", StringComparison.Ordinal));

        var image = Assert.Single(ProductsCatalog.Parse(xml));

        Assert.Equal(["Core", "Professional"], image.Editions);
    }

    [Fact]
    public void Parse_FileNameWithoutBuildStamp_StillYieldsTheImage()
    {
        var xml = Catalog(GoodFile
            .Replace("{0}", "Core", StringComparison.Ordinal)
            .Replace("26100.4349.250607-1500.ge_release_svc_refresh_CLIENTCONSUMER", "custom_CLIENTCONSUMER", StringComparison.Ordinal));

        var image = Assert.Single(ProductsCatalog.Parse(xml));

        Assert.Null(image.Build);
        Assert.Null(image.Built);
        Assert.Equal(EsdChannel.Consumer, image.Channel);
    }

    [Fact]
    public void Parse_NotXml_IsRejected()
    {
        Assert.Throws<InvalidDataException>(() => ProductsCatalog.Parse("<MCT><File>"u8.ToArray()));
    }

    [Fact]
    public void Parse_DoctypeWithEntities_IsRejected()
    {
        var bomb = Encoding.UTF8.GetBytes("""
            <?xml version="1.0"?>
            <!DOCTYPE MCT [<!ENTITY a "aaaaaaaaaa"><!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;">]>
            <MCT>&b;</MCT>
            """);

        Assert.Throws<InvalidDataException>(() => ProductsCatalog.Parse(bomb));
    }
}
