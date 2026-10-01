// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Workshop.Licensing;

namespace Bootrix.Core.Tests.Workshop;

public class Oa3DescriptionTests
{
    [Theory]
    [InlineData("[4.0] Professional OEM:DM", "Professional", "OEM", "DM")]
    [InlineData("[4.0] Core OEM:DM", "Core", "OEM", "DM")]
    [InlineData("[4.0] CoreSingleLanguage OEM:DM", "CoreSingleLanguage", "OEM", "DM")]
    [InlineData("[4.0] ProfessionalWorkstation OEM:DM", "ProfessionalWorkstation", "OEM", "DM")]
    [InlineData("[4.0] ProfessionalN OEM:DM", "ProfessionalN", "OEM", "DM")]
    [InlineData("Professional OEM:DM", "Professional", "OEM", "DM")]
    [InlineData("  [4.0]   Professional   OEM:DM  ", "Professional", "OEM", "DM")]
    [InlineData("[4.0] Professional OEM:SLP", "Professional", "OEM", "SLP")]
    [InlineData("[4.0] Professional OEM", "Professional", "OEM", null)]
    [InlineData("[4.0] Enterprise Volume:MAK", "Enterprise", "Volume", "MAK")]
    [InlineData("[4.0] professional oem:dm", "Professional", "OEM", "dm")]
    [InlineData("Windows 10 Professional OEM:DM", "Professional", "OEM", "DM")]
    [InlineData("[4.0][2.1] Core OEM:DM", "Core", "OEM", "DM")]
    public void Parse_UnderstandsTheKnownShapes(string text, string edition, string channel, string? subtype)
    {
        var parsed = Oa3Description.Parse(text);

        Assert.NotNull(parsed);
        Assert.Equal(edition, parsed.EditionId);
        Assert.Equal(channel, parsed.Channel);
        Assert.Equal(subtype, parsed.Subtype);
        Assert.Equal(text.Trim(), parsed.Raw);
    }

    [Theory]
    [InlineData("[4.0] Ultimate OEM:DM", null, "OEM")]
    [InlineData("[4.0] OEM:DM", null, "OEM")]
    [InlineData("[4.0] Professional", "Professional", null)]
    [InlineData("something nobody documented", null, null)]
    [InlineData("[4.0]", null, null)]
    public void Parse_UnfamiliarText_KeepsWhatItCanAndNeverGuesses(string text, string? edition, string? channel)
    {
        var parsed = Oa3Description.Parse(text);

        Assert.NotNull(parsed);
        Assert.Equal(edition, parsed.EditionId);
        Assert.Equal(channel, parsed.Channel);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_Empty_ReturnsNull(string? text)
    {
        Assert.Null(Oa3Description.Parse(text));
    }

    [Theory]
    [InlineData("Windows 11", "Professional", "Windows 11 Pro")]
    [InlineData("Windows 11", "Core", "Windows 11 Home")]
    [InlineData("Windows 10", "CoreSingleLanguage", "Windows 10 Home Single Language")]
    [InlineData("Windows 11", "ProfessionalWorkstation", "Windows 11 Pro for Workstations")]
    [InlineData("Windows 11", "ProfessionalEducation", "Windows 11 Pro Education")]
    [InlineData("Windows 11", "professional", "Windows 11 Pro")]
    [InlineData("Windows 11", "CoreN", "Windows 11 Home N")]
    [InlineData("Windows 11", "Enterprise", "Windows 11 Enterprise")]
    public void ImageName_MapsEditionIdsToTheNamesInInstallWim(string product, string editionId, string expected)
    {
        Assert.Equal(expected, WindowsEditions.ImageName(product, editionId));
    }

    [Theory]
    [InlineData("ServerStandard")]
    [InlineData("Ultimate")]
    [InlineData("")]
    [InlineData(null)]
    public void ImageName_UnknownEdition_IsNull(string? editionId)
    {
        Assert.Null(WindowsEditions.ImageName("Windows 11", editionId));
        Assert.Null(WindowsEditions.Canonical(editionId));
    }
}
