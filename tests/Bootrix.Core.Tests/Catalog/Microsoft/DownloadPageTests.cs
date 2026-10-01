// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Catalog.Microsoft;
using Bootrix.Core.Net;

namespace Bootrix.Core.Tests.Catalog.Microsoft;

public class DownloadPageTests
{
    private static readonly string Row = "<tr><td>German 64-bit</td><td>193BBDE65EC84E298A1C798959489C8375ED501CF973C6D37FA52BB22EE8D43B</td></tr>";

    private static string Page(string body) => $"<html><body>{body}</body></html>";

    [Fact]
    public void Parse_RealWindows11Page_ReadsEditionAndPublishedDigests()
    {
        var page = DownloadPage.Parse(MicrosoftFixtures.Text("page-windows11.html"));

        var edition = Assert.Single(page.Editions);
        Assert.Equal(3813, edition.Id);
        Assert.Equal("Windows 11 (multi-edition ISO for x64 devices)", edition.Label);
        Assert.Equal("https://www.microsoft.com/software-download-connector/api/", page.ApiBase.AbsoluteUri);
        Assert.StartsWith("https://ov-df.microsoft.com/mdt.js?instanceId=560dc9f3-1aa5-4a2f-b63c-9e18f8d0e175", page.FingerprintScript.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal(38, page.Hashes.Count);
        Assert.Equal(
            new FileHash(HashKind.Sha256, "193BBDE65EC84E298A1C798959489C8375ED501CF973C6D37FA52BB22EE8D43B"),
            page.FindHash(["German", "German"], 64));
    }

    [Fact]
    public void Parse_RealArm64Page_ReadsTheTrimmedEditionLabel()
    {
        var page = DownloadPage.Parse(MicrosoftFixtures.Text("page-windows11arm64.html"));

        var edition = Assert.Single(page.Editions);
        Assert.Equal(3816, edition.Id);
        Assert.Equal("Windows 11 (multi-edition ISO for Arm64)", edition.Label);
        Assert.Equal(38, page.Hashes.Count);
    }

    [Fact]
    public void Parse_RealWindows10Page_HasHashesForBothWordSizes()
    {
        var page = DownloadPage.Parse(MicrosoftFixtures.Text("page-windows10iso.html"));

        Assert.Equal(2618, Assert.Single(page.Editions).Id);
        Assert.Equal(76, page.Hashes.Count);
        Assert.Equal("D1A41A09E9AE09631A087EDF95D7F4EECAB622F88B3C824D856CFEA47FCC0B4C", page.FindHash(["German"], 64)!.Hex.ToUpperInvariant());
        Assert.Equal("B0BFC1B9B176DF0303ED3A91E7332CD1A8B57B07F25752CEC9493E1333F88075", page.FindHash(["German"], 32)!.Hex.ToUpperInvariant());
    }

    [Theory]
    [InlineData("English (United Kingdom)", "English International", "7E3F373BD3C2321B5D5125DFCF718DFBF1A0ABC50A267118669951890DF98A5D")]
    [InlineData("English", "English (United States)", "BD4307DF32BC8AF33B39CCECB1174AEB345386630F89A2B86C7A4E36B55EA650")]
    [InlineData("Chinese (Simplified)", "Chinese Simplified", "B795B52D598BF8FDEA5975844731F4A50C5713876CEFC513884354272B023AF0")]
    [InlineData("Spanish (Mexico)", "Spanish (Mexico)", "83231BCD4C509CFA51EB9A79056C6F1CA40DF09110267832E780A881B9EE8F9E")]
    public void FindHash_LanguageNamesThatDifferBetweenApiAndTable_AreMatchedThroughEitherSpelling(string language, string localized, string expected)
    {
        var page = DownloadPage.Parse(MicrosoftFixtures.Text("page-windows11.html"));

        Assert.Equal(expected, page.FindHash([language, localized], 64)!.Hex.ToUpperInvariant());
    }

    [Fact]
    public void FindHash_EveryLanguageOfTheApiHasExactlyOneRow()
    {
        var page = DownloadPage.Parse(MicrosoftFixtures.Text("page-windows11.html"));
        var skus = MicrosoftApiResponses.ParseSkus(MicrosoftFixtures.Text("skus-3813.json")).Skus;

        Assert.Equal(38, skus.Count);
        Assert.All(skus, s => Assert.NotNull(page.FindHash([s.Language, s.LocalizedLanguage], 64)));
    }

    [Fact]
    public void FindHash_UnknownLanguageOrWordSize_YieldsNothing()
    {
        var page = DownloadPage.Parse(MicrosoftFixtures.Text("page-windows11.html"));

        Assert.Null(page.FindHash(["Klingon"], 64));
        Assert.Null(page.FindHash(["German"], 32));
    }

    [Fact]
    public void FindHash_TwoDifferentRowsForTheSameLanguage_AreNotGuessedBetween()
    {
        var other = Row.Replace("193BBDE6", "AAAAAAAA", StringComparison.Ordinal);
        var page = DownloadPage.Parse(Page($"<select id=\"product-edition\"><option value=\"1\">X</option></select><table><tbody>{Row}{other}</tbody></table>"));

        Assert.Equal(2, page.Hashes.Count);
        Assert.Null(page.FindHash(["German"], 64));
    }

    [Fact]
    public void FindHash_SameRowTwice_IsStillUnique()
    {
        var page = DownloadPage.Parse(Page($"<select id=\"product-edition\"><option value=\"1\">X</option></select><table><tbody>{Row}{Row}</tbody></table>"));

        Assert.NotNull(page.FindHash(["German"], 64));
    }

    [Fact]
    public void Parse_PageWithoutHashTable_HasNoHashes()
    {
        var page = DownloadPage.Parse(Page("<select id=\"product-edition\"><option value=\"7\">Seven</option></select>"));

        Assert.Empty(page.Hashes);
        Assert.Null(page.FindHash(["German"], 64));
    }

    [Fact]
    public void Parse_DifferentMarkupStyles_AreAccepted()
    {
        // Single quotes, other attribute order, unclosed options and entities in the label.
        var html = Page("""
            <SELECT class='wide' ID='product-edition-arm'>
              <option value='null' selected>Choose</option>
              <option data-x="1" VALUE='11'>Windows &amp; Friends
              <option value="12">Second</option>
            </SELECT>
            <select id="product-edition-a"><option value="12">Second again</option><option value="13">Third</option></select>
            """);

        var editions = DownloadPage.Parse(html).Editions;

        Assert.Equal([11, 12, 13], editions.Select(e => e.Id));
        Assert.Equal("Windows & Friends", editions[0].Label);
        Assert.Equal("Second", editions[1].Label);
    }

    [Fact]
    public void Parse_SelectsThatAreNotTheEditionList_AreIgnored()
    {
        var html = Page("""
            <select id="product-languages"><option value="99">Wrong</option></select>
            <select data-id="product-edition"><option value="98">Wrong too</option></select>
            <select id="product-edition"><option value="5">Right</option></select>
            """);

        Assert.Equal([5], DownloadPage.Parse(html).Editions.Select(e => e.Id));
    }

    [Theory]
    [InlineData("<p>nothing here</p>")]
    [InlineData("<select id=\"product-edition\"><option value=\"null\">Select</option></select>")]
    [InlineData("<select id=\"product-edition\"><option value=\"abc\">Text id</option></select>")]
    [InlineData("")]
    public void Parse_PageWithoutEditionList_ReportsTheChangedLayout(string body)
    {
        var error = Assert.Throws<InvalidDataException>(() => DownloadPage.Parse(Page(body)));

        Assert.Contains("product-edition", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_AddressesNamedByThePage_AreUsedWhenTheyAreHttps()
    {
        var html = Page("""
            <input id="endpoint-svc" type="hidden" value="https://example.test/api/"/>
            <input type="hidden" value="https://example.test/mdt.js?session_id=" id="ov-df-ref"/>
            <select id="product-edition"><option value="1">X</option></select>
            """);

        var page = DownloadPage.Parse(html);

        Assert.Equal("https://example.test/api/", page.ApiBase.AbsoluteUri);
        Assert.Equal("https://example.test/mdt.js?session_id=", page.FingerprintScript.AbsoluteUri);
    }

    [Theory]
    [InlineData("http://example.test/api/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/relative/api/")]
    [InlineData("")]
    public void Parse_AddressesThatAreNotAbsoluteHttps_FallBackToTheDefaults(string value)
    {
        var html = Page($"<input id=\"endpoint-svc\" value=\"{value}\"/><select id=\"product-edition\"><option value=\"1\">X</option></select>");

        Assert.Equal("https://www.microsoft.com/software-download-connector/api/", DownloadPage.Parse(html).ApiBase.AbsoluteUri);
    }

    [Fact]
    public void Parse_LookAlikeAttributes_AreNotTakenForTheRealOnes()
    {
        var html = Page("""
            <input data-id="endpoint-svc" value="https://evil.test/api/"/>
            <input id="endpoint-svc" data-value="https://evil.test/api/"/>
            <select id="product-edition"><option value="1">X</option></select>
            """);

        Assert.Equal("https://www.microsoft.com/software-download-connector/api/", DownloadPage.Parse(html).ApiBase.AbsoluteUri);
    }
}
