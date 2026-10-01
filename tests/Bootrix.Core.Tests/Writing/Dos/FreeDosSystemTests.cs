// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using Bootrix.Core.Boot.Dos;
using Bootrix.Core.FileSystems.Fat;

namespace Bootrix.Core.Tests.Writing.Dos;

public class FreeDosSystemTests
{
    // The same hashes as assets/third-party/freedos/SHA256SUMS: an embedded program that is swapped must show up here.
    [Theory]
    [InlineData("KERNL386.SYS", "932c0c155701eddb7b902f7269a1b2ce31f5c82a6dc195172f2336d18a74e1fb")]
    [InlineData("KERNL86.SYS", "f34a7483c575fcf2709d9a7d0bc3db81c6211c279530f9e1bf78576b9233924d")]
    [InlineData("COMMAND.COM", "077808379e896476f7f69d62e6c8989d8fc23e8ef58d1c8492db1ac106784107")]
    [InlineData("fat12com.bin", "0742aecaf453713e1895ee835143a133a6fb8f90374d1050e7064d6e4e290eeb")]
    [InlineData("fat16com.bin", "39586cd0f55f7732791a990770163d4af9827623e418203921d6f23819b94cb5")]
    [InlineData("fat32lba.bin", "eb86f238f70d559f3dc66d082fc55c8459c77b4fe9e2fa9d48c440cf4a7c9a74")]
    public void EmbeddedFreeDosFiles_AreTheOnesListedInSources(string name, string sha256)
    {
        Assert.Equal(sha256, Convert.ToHexStringLower(SHA256.HashData(DosAssets.FreeDos(name))));
    }

    [Theory]
    [InlineData("mbr.bin", "4746f74bc9b9d3d579c41988a4a29bb7ac932ad1c70470ea779ea161eb799b64")]
    [InlineData("mbr_f.bin", "045aa462391c89e05375d7c45f3052fe3ab0472b5b97100e04fc1812621985e2")]
    public void EmbeddedMbrCode_IsTheSyslinuxMbrListedInSources(string name, string sha256)
    {
        Assert.Equal(sha256, Convert.ToHexStringLower(SHA256.HashData(DosAssets.Mbr(name))));
    }

    [Fact]
    public void Default_HasTheSystemFilesFirstAndHidden()
    {
        var system = FreeDosSystem.Create();

        Assert.Equal(["\\KERNEL.SYS", "\\COMMAND.COM", "\\FDCONFIG.SYS", "\\AUTOEXEC.BAT"], system.Files.Select(f => f.Path));
        Assert.Equal(DosFile.SystemFile, system.Files[0].Attributes);
        Assert.Equal(DosFile.SystemFile, system.Files[1].Attributes);
        Assert.Equal(FileAttributes.Archive, system.Files[3].Attributes);
        Assert.Equal(DosAssets.FreeDos("COMMAND.COM"), system.Files[1].Content);
        Assert.True(system.TotalBytes < 400_000, "the minimal system stays small");
    }

    [Fact]
    public void Default_UsesThe386KernelWithForcedLba()
    {
        var kernel = FreeDosSystem.Create().Files[0].Content;
        var original = DosAssets.FreeDos("KERNL386.SYS");

        Assert.Equal(original.Length, kernel.Length);
        Assert.Equal(1, kernel[0x0D]);
        Assert.Equal(0, original[0x0D]);
        Assert.Equal(original.Where((b, i) => i != 0x0D), kernel.Where((b, i) => i != 0x0D));
    }

    [Fact]
    public void LbaIsNotForced_WhenTheCallerAsksForTheCautiousVariant()
    {
        var kernel = FreeDosSystem.Create(forceLba: false).Files[0].Content;

        Assert.Equal(DosAssets.FreeDos("KERNL386.SYS"), kernel);
    }

    [Fact]
    public void Floppy_GetsTheKernelThatRunsOnAn8086_Unpatched()
    {
        var kernel = FreeDosSystem.Create(floppy: true).Files[0].Content;

        Assert.Equal(DosAssets.FreeDos("KERNL86.SYS"), kernel);
    }

    [Theory]
    [InlineData(DosKernel.I386, false, "KERNL386.SYS")]
    [InlineData(DosKernel.I8086, false, "KERNL86.SYS")]
    [InlineData(DosKernel.I386, true, "KERNL386.SYS")]
    [InlineData(DosKernel.I8086, true, "KERNL86.SYS")]
    public void AnExplicitKernel_WinsOverTheMedium(DosKernel kernel, bool floppy, string expected)
    {
        var content = FreeDosSystem.Create(kernel, floppy).Files[0].Content;

        Assert.Equal(DosAssets.FreeDos(expected).Length, content.Length);
    }

    [Fact]
    public void WithFile_ReplacesAFileByPath_OrAppends()
    {
        var system = FreeDosSystem.Create();

        var replaced = system.WithFile(DosFile.Text("\\autoexec.bat", "ECHO HI\n"));
        var added = system.WithFile(DosFile.Text("\\NOTE.TXT", "x"));

        Assert.Equal(system.Files.Count, replaced.Files.Count);
        Assert.Equal("ECHO HI\r\n"u8.ToArray(), replaced.Files[3].Content);
        Assert.Equal("\\NOTE.TXT", added.Files[^1].Path);
        Assert.Equal(system.Files.Count + 1, added.Files.Count);
        Assert.Equal(4, system.Files.Count);
    }

    [Fact]
    public void Customize_AddsTheLoaderOfTheVolumeType()
    {
        var system = FreeDosSystem.Create();

        var options = system.Customize(new FatFormatOptions { TotalBytes = 64 * 1024 * 1024 }, 131072);

        Assert.Equal(FreeDosBootSector.CodeFor(FatType.Fat16), options.BootCode);
    }

    [Fact]
    public void Text_UsesDosLineEndings()
    {
        Assert.Equal("A\r\nB\r\n"u8.ToArray(), DosFile.Text("\\X.TXT", "A\nB\n").Content);
    }

    [Theory]
    [InlineData("\\KERNEL.SYS", "")]
    [InlineData("\\LOCALE\\EGA.CPI", "\\LOCALE")]
    public void Folder_IsEmptyForTheRoot(string path, string folder)
    {
        Assert.Equal(folder, new DosFile(path, []).Folder);
    }
}
