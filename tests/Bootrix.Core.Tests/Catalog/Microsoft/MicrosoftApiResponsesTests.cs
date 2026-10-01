// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Catalog.Microsoft;

namespace Bootrix.Core.Tests.Catalog.Microsoft;

public class MicrosoftApiResponsesTests
{
    [Fact]
    public void ParseSkus_RealWindows11List_ReadsLanguagesWithTheirIds()
    {
        var response = MicrosoftApiResponses.ParseSkus(MicrosoftFixtures.Text("skus-3813.json"));

        Assert.Empty(response.Errors);
        Assert.Equal(38, response.Skus.Count);
        Assert.Equal(new SkuEntry("27122", "Arabic", "Arabic", "Windows 11 Client - Build 26300.9457"), response.Skus[0]);

        var british = Assert.Single(response.Skus, s => s.Language == "English (United Kingdom)");
        Assert.Equal("English International", british.LocalizedLanguage);
        Assert.Equal("27126", Assert.Single(response.Skus, s => s.Language == "German").Id);
    }

    [Fact]
    public void ParseSkus_RealWindows10List_CarriesTheReleaseInTheProductName()
    {
        var response = MicrosoftApiResponses.ParseSkus(MicrosoftFixtures.Text("skus-2618.json"));

        Assert.Equal(38, response.Skus.Count);
        Assert.All(response.Skus, s => Assert.Equal("Windows 10 22H2_v1", s.ProductName));
        Assert.Equal("16073", Assert.Single(response.Skus, s => s.Language == "German").Id);
    }

    [Fact]
    public void ParseLinks_RealWindows11Answer_ReadsTheSixtyFourBitLinkAndItsExpiry()
    {
        var response = MicrosoftApiResponses.ParseLinks(MicrosoftFixtures.Text("links-3813-de.json"));

        var link = Assert.Single(response.Links);
        Assert.Equal("x64", link.Architecture);
        Assert.Equal("German", link.Language);
        Assert.Equal("Windows 11 Client - Build 26300.9457", link.ProductName);
        Assert.Equal("software.download.prss.microsoft.com", link.Url.Host);
        Assert.EndsWith("/dbazure/Windows11_Client_x64_de-de_26300_9457.iso", link.Url.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 7, 7, 15, TimeSpan.Zero).AddTicks(3_761_609), response.Expires);
        Assert.Empty(response.Errors);
    }

    [Fact]
    public void ParseLinks_RealWindows10Answer_OffersThirtyTwoAndSixtyFourBit()
    {
        var response = MicrosoftApiResponses.ParseLinks(MicrosoftFixtures.Text("links-2618-de.json"));

        Assert.Equal(["x86", "x64"], response.Links.Select(l => l.Architecture));
        Assert.EndsWith("Win10_22H2_German_x32v1.iso", response.Links[0].Url.AbsolutePath, StringComparison.Ordinal);
        Assert.EndsWith("Win10_22H2_German_x64v1.iso", response.Links[1].Url.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseLinks_RealArm64Answer_UsesDownloadTypeTwo()
    {
        var link = Assert.Single(MicrosoftApiResponses.ParseLinks(MicrosoftFixtures.Text("links-3816-de.json")).Links);

        Assert.Equal("arm64", link.Architecture);
        Assert.Equal("Windows 11 ARM 64 26H2 26300.9457", link.ProductName);
    }

    [Fact]
    public void ParseLinks_RealRefusal_ReportsTheSentinelErrorAndNoLinks()
    {
        var response = MicrosoftApiResponses.ParseLinks(MicrosoftFixtures.Text("links-rejected.json"));

        Assert.Empty(response.Links);
        Assert.Null(response.Expires);
        var error = Assert.Single(response.Errors);
        Assert.Equal("ErrorSettings.SentinelReject", error.Key);
        Assert.True(error.IsSentinelReject);
        Assert.Equal("Sentinel marked this request as rejected.", error.Value);
    }

    [Theory]
    [InlineData("""{"ProductDownloadOptions":[{"Uri":"https://h.test/Windows_arm64_x.iso","DownloadType":9}]}""", "arm64")]
    [InlineData("""{"ProductDownloadOptions":[{"Uri":"https://h.test/Win_x32v1.iso"}]}""", "x86")]
    [InlineData("""{"ProductDownloadOptions":[{"Uri":"https://h.test/Win_x86.iso","DownloadType":"x"}]}""", "x86")]
    [InlineData("""{"ProductDownloadOptions":[{"Uri":"https://h.test/Win_x64.iso"}]}""", "x64")]
    [InlineData("""{"ProductDownloadOptions":[{"Uri":"https://h.test/Some.iso"}]}""", null)]
    public void ParseLinks_UnknownDownloadType_FallsBackToTheFileName(string json, string? expected)
    {
        Assert.Equal(expected, Assert.Single(MicrosoftApiResponses.ParseLinks(json).Links).Architecture);
    }

    [Fact]
    public void ParseLinks_DownloadTypeWinsOverTheFileName()
    {
        const string json = """{"ProductDownloadOptions":[{"Uri":"https://h.test/Win_x64.iso","DownloadType":0}]}""";

        Assert.Equal("x86", Assert.Single(MicrosoftApiResponses.ParseLinks(json).Links).Architecture);
    }

    [Fact]
    public void ParseLinks_ErrorsInBothPlaces_AreCollectedWithUnknownShapesKept()
    {
        const string json = """
            {"Errors":[{"Key":"A","Value":"top"}],
             "ValidationContainer":{"Errors":[{"Key":"B","Value":"nested"},{"Odd":1}]}}
            """;

        var errors = MicrosoftApiResponses.ParseLinks(json).Errors;

        Assert.Equal(["A: top", "B: nested", "unknown: {\"Odd\":1}"], errors.Select(e => e.ToString()));
        Assert.DoesNotContain(errors, e => e.IsSentinelReject);
    }

    [Theory]
    [InlineData("""{"Skus":null}""")]
    [InlineData("""{}""")]
    [InlineData("""{"Skus":"none"}""")]
    public void ParseSkus_MissingOrOddList_IsEmptyNotAnError(string json)
    {
        var response = MicrosoftApiResponses.ParseSkus(json);

        Assert.Empty(response.Skus);
        Assert.Empty(response.Errors);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("")]
    public void Parse_AnswersThatAreNoJsonObject_AreRejected(string json)
    {
        Assert.Throws<InvalidDataException>(() => MicrosoftApiResponses.ParseSkus(json));
        Assert.Throws<InvalidDataException>(() => MicrosoftApiResponses.ParseLinks(json));
    }

    [Theory]
    [InlineData("""{"Skus":[{"Language":"German"}]}""")]
    [InlineData("""{"Skus":[{"Id":"1"}]}""")]
    public void ParseSkus_EntryWithoutIdOrLanguage_IsRejected(string json)
    {
        Assert.Throws<InvalidDataException>(() => MicrosoftApiResponses.ParseSkus(json));
    }

    [Theory]
    [InlineData("""{"ProductDownloadOptions":[{"Name":"x"}]}""")]
    [InlineData("""{"ProductDownloadOptions":[{"Uri":"http://h.test/a.iso"}]}""")]
    [InlineData("""{"ProductDownloadOptions":[{"Uri":"/relative.iso"}]}""")]
    public void ParseLinks_EntryWithoutHttpsAddress_IsRejected(string json)
    {
        Assert.Throws<InvalidDataException>(() => MicrosoftApiResponses.ParseLinks(json));
    }

    [Fact]
    public void ParseSkus_NumericIds_AreAcceptedAsText()
    {
        var sku = Assert.Single(MicrosoftApiResponses.ParseSkus("""{"Skus":[{"Id":27126,"Language":"German"}]}""").Skus);

        Assert.Equal("27126", sku.Id);
        Assert.Equal("German", sku.LocalizedLanguage);
    }
}
