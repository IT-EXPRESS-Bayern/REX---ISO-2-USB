// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using Bootrix.Core.Boot.Syslinux;

namespace Bootrix.Core.Tests.Writing.Linux;

public class SyslinuxVersionTests
{
    [Theory]
    [InlineData("ISOLINUX 6.03 2014-10-06 Copyright (C) 1994-2014 H. Peter Anvin et al", 6, 3, "2014-10-06")]
    [InlineData("ISOLINUX 6.04 6.04-pre1 Copyright (C) 1994-2015 H. Peter Anvin et al", 6, 4, "6.04-pre1")]
    [InlineData("ISOLINUX 6.04 20190206 ", 6, 4, "20190206")]
    [InlineData("ISOLINUX 4.05 2011-12-09 Copyright", 4, 5, "2011-12-09")]
    [InlineData("SYSLINUX 6.03 2014-10-06", 6, 3, "2014-10-06")]
    [InlineData("EXTLINUX 6.04 6.04-pre1", 6, 4, "6.04-pre1")]
    [InlineData("PXELINUX 3.86 2010-11-12", 3, 86, "2010-11-12")]
    [InlineData("ISOLINUX 6.04", 6, 4, "")]
    [InlineData("xx\0ISOLINUX 6.04 6.04-pre1*\0yy", 6, 4, "6.04-pre1")]
    public void Parse_BannerOfASyslinuxBinary_GivesVersionAndTag(string banner, int major, int minor, string tag)
    {
        var version = SyslinuxVersion.Parse(banner);

        Assert.Equal(new SyslinuxVersion(major, minor, tag), version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("isolinux: Image checksum error, sector %d")]
    [InlineData("ISOLINUX x.yz")]
    [InlineData("LINUX 6.03")]
    public void Parse_TextWithoutABanner_GivesNull(string text)
    {
        Assert.Null(SyslinuxVersion.Parse(text));
    }

    [Fact]
    public void FromBinary_SkipsTheBootCodeAtTheStart()
    {
        var binary = new byte[256];
        Encoding.ASCII.GetBytes("ISOLINUX 3.00 old").CopyTo(binary, 10);
        Encoding.ASCII.GetBytes("ISOLINUX 6.04 6.04-pre1 Copyright").CopyTo(binary, 100);

        Assert.Equal(new SyslinuxVersion(6, 4, "6.04-pre1"), SyslinuxVersion.FromBinary(binary));
    }

    [Fact]
    public void FromBinary_ShortOrBannerlessInput_GivesNull()
    {
        Assert.Null(SyslinuxVersion.FromBinary(new byte[10]));
        Assert.Null(SyslinuxVersion.FromBinary(new byte[4096]));
    }

    [Fact]
    public void ToString_PutsTheTagBehindTheTwoDigitMinor()
    {
        Assert.Equal("6.03 2014-10-06", new SyslinuxVersion(6, 3, "2014-10-06").ToString());
        Assert.Equal("6.04", new SyslinuxVersion(6, 4, "").ToString());
    }

    [Fact]
    public void SameRelease_IgnoresTheTag()
    {
        Assert.True(new SyslinuxVersion(6, 4, "a").SameRelease(new SyslinuxVersion(6, 4, "b")));
        Assert.False(new SyslinuxVersion(6, 3, "a").SameRelease(new SyslinuxVersion(6, 4, "a")));
    }

    [Fact]
    public void ShippedCores_AnnounceTheirOwnRelease()
    {
        Assert.Equal(new SyslinuxVersion(6, 3, "2014-10-06"), SyslinuxBundle.Shipped.Single(b => b.Id == "6.03").Version);
        Assert.Equal(new SyslinuxVersion(6, 4, "6.04-pre1"), SyslinuxBundle.Shipped.Single(b => b.Id == "6.04-pre1").Version);
    }

    [Fact]
    public void DistributionIsolinux_IsRecognisedWhenInstalled()
    {
        const string path = "/usr/lib/ISOLINUX/isolinux.bin";
        if (!File.Exists(path))
        {
            return;
        }

        var version = SyslinuxVersion.FromBinary(File.ReadAllBytes(path));

        Assert.NotNull(version);
        Assert.Equal(6, version.Value.Major);
        Assert.Equal(4, version.Value.Minor);
    }
}

public class SyslinuxBundleTests
{
    [Theory]
    [InlineData(6, 3, "2014-10-06", "6.03", SyslinuxMatch.Exact)]
    [InlineData(6, 4, "6.04-pre1", "6.04-pre1", SyslinuxMatch.Exact)]
    [InlineData(6, 4, "20190206", "6.04-pre1", SyslinuxMatch.SameRelease)]
    [InlineData(6, 3, "debian", "6.03", SyslinuxMatch.SameRelease)]
    [InlineData(6, 0, "2013-02-14", "6.03", SyslinuxMatch.SameMajor)]
    [InlineData(6, 2, "", "6.03", SyslinuxMatch.SameMajor)]
    [InlineData(6, 9, "", "6.04-pre1", SyslinuxMatch.SameMajor)]
    [InlineData(5, 10, "", "6.04-pre1", SyslinuxMatch.Different)]
    [InlineData(4, 5, "2011-12-09", "6.04-pre1", SyslinuxMatch.Different)]
    [InlineData(3, 86, "", "6.04-pre1", SyslinuxMatch.Different)]
    public void Select_PicksTheClosestShippedRelease(int major, int minor, string tag, string expectedId, SyslinuxMatch expectedMatch)
    {
        var choice = SyslinuxBundle.Select(new SyslinuxVersion(major, minor, tag));

        Assert.Equal(expectedId, choice.Bundle.Id);
        Assert.Equal(expectedMatch, choice.Match);
    }

    [Fact]
    public void Select_WithoutAVersion_TakesTheNewestRelease()
    {
        var choice = SyslinuxBundle.Select(null);

        Assert.Equal("6.04-pre1", choice.Bundle.Id);
        Assert.Equal(SyslinuxMatch.Different, choice.Match);
    }

    [Fact]
    public void Shipped_BundlesCarryTheModulesTheInstallerNeeds()
    {
        foreach (var bundle in SyslinuxBundle.Shipped)
        {
            Assert.Equal(512, bundle.BootSector.Length);
            Assert.InRange(bundle.Core.Length, 60_000, 80_000);
            foreach (var name in new[] { "ldlinux.c32", "libcom32.c32", "libutil.c32", "menu.c32", "vesamenu.c32", "mboot.c32" })
            {
                Assert.True(bundle.TryGetModule(name, out var module), name);
                Assert.True(module.Length > 1000, name);
            }

            Assert.False(bundle.TryGetModule("missing.c32", out _));
        }
    }

    // The files are pinned in assets/third-party/SOURCES.md; a changed byte must be a decision, not an accident.
    [Theory]
    [InlineData("6.03", "ldlinux.sys", "3f1206e0cc45dbe180e73adaeb221bfc7d5a800095738549390379d7d0282ac3")]
    [InlineData("6.03", "ldlinux.bss", "8814e576abc1aa44dde943b0caaee833a5810142614adeeb4cc725e78a5045b7")]
    [InlineData("6.03", "ldlinux.c32", "5cef9ad0d0ca04097262241686c6c3a7306ab9b9cdf24b9d4ee3b16af01a5af2")]
    [InlineData("6.04-pre1", "ldlinux.sys", "73b62767a16200b9af193a7d5c94e9c294c6dbb6d5b17c15038c9f3173c9a7bc")]
    [InlineData("6.04-pre1", "ldlinux.bss", "cc40ba0349782cb4c9021e54dcc0a4540c3a8b96088b3a5648671926ef44d2f0")]
    [InlineData("6.04-pre1", "ldlinux.c32", "d3472c02263acf9cd1da5db51e263c5484bad13ea68618c403d9cb01ca070aee")]
    public void EmbeddedFiles_MatchThePinnedHashes(string release, string file, string sha256)
    {
        var bundle = SyslinuxBundle.Shipped.Single(b => b.Id == release);
        var bytes = file switch
        {
            "ldlinux.sys" => bundle.Core.ToArray(),
            "ldlinux.bss" => bundle.BootSector.ToArray(),
            _ => bundle.LdlinuxModule.ToArray(),
        };

        Assert.Equal(sha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    [Theory]
    [InlineData("mbr.bin", "4746f74bc9b9d3d579c41988a4a29bb7ac932ad1c70470ea779ea161eb799b64")]
    [InlineData("mbr_f.bin", "045aa462391c89e05375d7c45f3052fe3ab0472b5b97100e04fc1812621985e2")]
    [InlineData("gptmbr.bin", "d2a9081727f91f4c38494e52cdeb86ebd9009fead17a739effbad4011c581d1f")]
    public void MbrCode_MatchesThePinnedHashes(string name, string sha256)
    {
        var code = name switch
        {
            "mbr.bin" => SyslinuxMbr.Code(gpt: false),
            "mbr_f.bin" => SyslinuxMbr.Code(gpt: false, forceDrive80: true),
            _ => SyslinuxMbr.Code(gpt: true),
        };

        Assert.Equal(SyslinuxMbr.CodeLength, code.Length);
        Assert.Equal(sha256, Convert.ToHexStringLower(SHA256.HashData(code)));
    }

    [Fact]
    public void MbrWrite_ReplacesTheBootCodeAndKeepsSignatureAndTable()
    {
        var disk = new MemoryStream(new byte[4096]);
        var sector = new byte[512];
        sector[440] = 0xAA;
        sector[446] = 0x80;
        sector[510] = 0x55;
        sector[511] = 0xAA;
        disk.Write(sector);

        SyslinuxMbr.Write(disk, gpt: false);

        var written = disk.ToArray();
        Assert.Equal(SyslinuxMbr.Code(gpt: false), written[..440]);
        Assert.Equal(sector[440..], written[440..512]);
    }
}
