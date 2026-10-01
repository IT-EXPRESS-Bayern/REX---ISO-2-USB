// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bootrix.Core.Localization;
using Bootrix.Core.Optical;
using Bootrix.Core.Presentation;

namespace Bootrix.Core.Tests.Presentation;

public class DiscViewTests
{
    private static readonly Localizer English = new() { Culture = CultureInfo.GetCultureInfo("en-US") };
    private static readonly Localizer German = new() { Culture = CultureInfo.GetCultureInfo("de-DE") };

    private static OpticalDrive Recorder() => new()
    {
        Id = "id1",
        Vendor = "ASUS",
        Product = "BW-16D1HT",
        DriveLetter = "G:",
        Capabilities = OpticalCapabilities.CdR | OpticalCapabilities.DvdPlusR,
    };

    private static OpticalMedia BlankDvd() => new()
    {
        Type = OpticalMediaType.DvdPlusR,
        State = OpticalMediaState.Blank,
        IsSupported = true,
        FreeSectors = 2_295_104,
        TotalSectors = 2_295_104,
    };

    [Fact]
    public void ARecorderWithABlankDiscIsDescribedWithTypeConditionAndFreeSpace()
    {
        var description = DiscView.Describe(Recorder(), BlankDvd(), English);

        Assert.Equal("G: ASUS BW-16D1HT", description.Title);
        Assert.Equal("DVD+R, blank, 4.38 GB free", description.Details);
        Assert.True(description.CanWrite);
        Assert.True(description.HasDisc);
    }

    [Fact]
    public void AnEmptyTrayIsSaidInPlainWords()
    {
        var description = DiscView.Describe(Recorder(), OpticalMedia.None, German);

        Assert.Equal("keine Disc eingelegt", description.Details);
        Assert.False(description.HasDisc);
    }

    [Fact]
    public void AReadOnlyDriveSaysSo()
    {
        var reader = Recorder() with { Capabilities = OpticalCapabilities.None };

        var description = DiscView.Describe(reader, new OpticalMedia { Type = OpticalMediaType.CdRom, State = OpticalMediaState.FinalSession, IsSupported = true }, English);

        Assert.False(description.CanWrite);
        Assert.StartsWith("read only", description.Details, StringComparison.Ordinal);
        Assert.Contains("CD-ROM", description.Details, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(OpticalMediaType.DvdMinusRDualLayer, "DVD-R DL")]
    [InlineData(OpticalMediaType.BdRe, "BD-RE")]
    [InlineData(OpticalMediaType.CdRw, "CD-RW")]
    public void TypesAreNamedAsPrintedOnTheDisc(OpticalMediaType type, string expected)
    {
        Assert.Equal(expected, DiscView.TypeName(type));
    }

    [Fact]
    public void EveryMediaConditionHasATextInBothLanguages()
    {
        foreach (var condition in Enum.GetValues<OpticalMediaCondition>())
        {
            Assert.True(English.Has("Disc.Media." + condition), condition.ToString());
            Assert.True(German.Has("Disc.Media." + condition), condition.ToString());
        }
    }
}
