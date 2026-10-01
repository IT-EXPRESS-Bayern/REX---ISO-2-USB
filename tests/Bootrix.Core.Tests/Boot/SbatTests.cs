// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Boot;
using Bootrix.Core.Errors;

namespace Bootrix.Core.Tests.Boot;

public class SbatTests
{
    // The newest block of rhboot/shim SbatLevel_Variable.txt when the snapshot was taken (November 2025 GRUB CVEs).
    private const string Level = "sbat,1,2025112400\nshim,4\ngrub,6\n";

    private static IReadOnlyList<SbatEntry> ParseFixture(string name) => SbatEntry.ParseSection(Fixtures.Read(name));

    [Fact]
    public void ParseSection_RealDebianShim_ReadsEveryLine()
    {
        var entries = ParseFixture("shim-debian-16.1.sbat");

        Assert.Equal(
            [("sbat", 1), ("shim", 4), ("shim.debian", 1)],
            entries.Select(e => (e.Component, e.Generation)));
        Assert.Equal("Debian", entries[2].VendorName);
    }

    [Fact]
    public void ParseSection_RealUbuntuShim15_8_ReadsComponents()
    {
        var entries = ParseFixture("shim-ubuntu-15.8.sbat");

        Assert.Contains(entries, e => e is { Component: "shim", Generation: 4 });
        Assert.Contains(entries, e => e is { Component: "shim.ubuntu", Generation: 1 });
    }

    [Fact]
    public void ParseSection_RealAlmaLinuxGrub_IgnoresTheNulPadding()
    {
        var entries = ParseFixture("grub-almalinux-2.12.sbat");

        Assert.Equal(["sbat", "grub", "grub.rh", "grub.centos", "grub.almalinux"], entries.Select(e => e.Component));
        Assert.Equal(5, entries.Single(e => e.Component == "grub").Generation);
    }

    [Fact]
    public void FindViolations_RealUbuntuGrub2_12_IsBelowTheNovember2025Level()
    {
        var violations = SbatLevel.Parse(Level).FindViolations(ParseFixture("grub-ubuntu-2.12.sbat"));

        var violation = Assert.Single(violations);
        Assert.Equal(new SbatViolation("grub", 5, 6), violation);
    }

    [Theory]
    [InlineData("shim-debian-16.1.sbat")]
    [InlineData("shim-ubuntu-15.8.sbat")]
    [InlineData("shim-almalinux-16.1.sbat")]
    public void FindViolations_RealShimsFrom15_8On_MeetTheShimGeneration(string fixture)
    {
        Assert.Empty(SbatLevel.Parse(Level).FindViolations(ParseFixture(fixture)));
    }

    [Fact]
    public void FindViolations_ComponentsNotInTheLevelAreIgnored()
    {
        var level = SbatLevel.Parse(Level);

        Assert.Empty(level.FindViolations([new SbatEntry("grub.debian", 1), new SbatEntry("sbat", 1)]));
    }

    [Fact]
    public void FindViolations_GenerationZeroIsSkipped()
    {
        Assert.Empty(SbatLevel.Parse(Level).FindViolations([new SbatEntry("grub", 0)]));
    }

    [Fact]
    public void FindViolations_EqualGenerationPasses()
    {
        var level = SbatLevel.Parse(Level);

        Assert.Empty(level.FindViolations([new SbatEntry("grub", 6), new SbatEntry("shim", 4)]));
        Assert.Equal(2, level.FindViolations([new SbatEntry("grub", 5), new SbatEntry("shim", 3)]).Count);
    }

    [Fact]
    public void SbatLevel_Parse_ReadsDateAndComponents()
    {
        var level = SbatLevel.Parse("sbat,1,2025051000\nshim,4\ngrub,5\ngrub.proxmox,2\n");

        Assert.Equal("2025051000", level.LevelDate);
        Assert.Equal(2, level.MinimumGenerations["grub.proxmox"]);
        Assert.Equal(3, level.MinimumGenerations.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("shim,4\n")]
    [InlineData("sbat,1,2025112400\nshim,four\n")]
    [InlineData("sbat,1,2025112400\nshim\n")]
    public void SbatLevel_Parse_RejectsMalformedText(string text)
    {
        var ex = Assert.Throws<BootrixException>(() => SbatLevel.Parse(text));

        Assert.Equal(ErrorCode.RevocationDataInvalid, ex.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n\n\n")]
    [InlineData("garbage without commas")]
    [InlineData("shim,notanumber,x\n")]
    [InlineData("shim,-3,x\n")]
    [InlineData(",5,nameless\n")]
    [InlineData("shim,99999999999999999999,x\n")]
    public void ParseSection_UnusableLinesAreSkipped(string text)
    {
        Assert.Empty(SbatEntry.ParseSection(System.Text.Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void ParseSection_HandlesWindowsLineEndingsAndMissingTrailingNewline()
    {
        var entries = SbatEntry.ParseSection("sbat,1,x\r\nshim,4,UEFI shim,shim,1,url"u8);

        Assert.Equal([("sbat", 1), ("shim", 4)], entries.Select(e => (e.Component, e.Generation)));
    }

    [Fact]
    public void ParseSection_StopsAtTheFirstNul()
    {
        var entries = SbatEntry.ParseSection("shim,4\0grub,9\n"u8);

        Assert.Equal("shim", Assert.Single(entries).Component);
    }

    [Fact]
    public void ParseSection_OversizedGarbageIsBounded()
    {
        var data = new byte[5 * 1024 * 1024];
        Array.Fill(data, (byte)'a');
        for (var i = 20; i < data.Length; i += 30)
        {
            data[i] = (byte)',';
        }

        var entries = SbatEntry.ParseSection(data);

        Assert.True(entries.Count <= 256);
    }

    [Fact]
    public void EfiBinary_ReadSbat_ReadsTheSectionOfAnImage()
    {
        var image = PeBuilder.Typical().AddSection(".sbat", [.. Fixtures.Read("shim-debian-16.1.sbat"), .. new byte[300]]).Build();

        var entries = EfiBinary.Parse(image).ReadSbat();

        Assert.NotNull(entries);
        Assert.Equal("shim.debian", entries[^1].Component);
    }

    [Fact]
    public void EfiBinary_ReadSbat_ImageWithoutSectionReturnsNull()
    {
        Assert.Null(EfiBinary.Parse(PeBuilder.Typical().Build()).ReadSbat());
    }
}
