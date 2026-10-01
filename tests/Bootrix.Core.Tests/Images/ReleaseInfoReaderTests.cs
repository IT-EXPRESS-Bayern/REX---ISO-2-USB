// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images;

namespace Bootrix.Core.Tests.Images;

public sealed class ReleaseInfoReaderTests
{
    private static string? Read(params (string Path, string Content)[] files)
    {
        var map = files.ToDictionary(file => file.Path, file => file.Content, StringComparer.OrdinalIgnoreCase);
        return ReleaseInfoReader.Read(path => map.GetValueOrDefault(path));
    }

    [Fact]
    public void DiskInfo_ReturnsItsFirstLineTrimmed() =>
        Assert.Equal(
            "Ubuntu 24.04 LTS \"Noble Numbat\" - Release amd64 (20240424)",
            Read((".disk/info", "Ubuntu 24.04 LTS \"Noble Numbat\" - Release amd64 (20240424)  \nsecond line\n")));

    [Fact]
    public void DiskInfo_WithTrailingNulBytes_IsCleaned() =>
        Assert.Equal("Debian GNU/Linux 12.5.0", Read((".disk/info", "Debian GNU/Linux 12.5.0\0\0\0")));

    [Fact]
    public void TreeInfo_CombinesNameAndVersion() =>
        Assert.Equal(
            "Fedora 40",
            Read((".treeinfo", "[general]\narch = x86_64\nfamily = Fedora\nname = Fedora\nversion = 40\n")));

    [Fact]
    public void TreeInfo_WithoutName_UsesTheFamily() =>
        Assert.Equal("Rocky Linux 9.3", Read((".treeinfo", "[general]\nfamily = Rocky Linux\nversion = 9.3\n")));

    [Fact]
    public void TreeInfo_WithoutVersion_ReturnsTheNameAlone() =>
        Assert.Equal("CentOS Stream", Read((".treeinfo", "[general]\nname = CentOS Stream\n")));

    [Fact]
    public void SuseContentFile_ReturnsTheProductLine() =>
        Assert.Equal("openSUSE-Tumbleweed", Read(("content", "CONTENTSTYLE 11\nPRODUCT openSUSE-Tumbleweed\nVERSION 20240101\n")));

    [Theory]
    [InlineData("Clonezilla-Live-Version", "clonezilla-live-3.1.2-9")]
    [InlineData("GParted-Live-Version", "gparted-live-1.6.0-3")]
    public void LiveToolMarkers_AreUsedAsALastResort(string file, string text) =>
        Assert.Equal(text, Read((file, text + "\n")));

    [Fact]
    public void EmptyDiskInfo_FallsThroughToTheNextSource() =>
        Assert.Equal("Fedora 39", Read((".disk/info", "   \n"), (".treeinfo", "[general]\nname = Fedora\nversion = 39\n")));

    [Fact]
    public void DiskInfo_TakesPrecedenceOverTreeInfo() =>
        Assert.Equal("Ubuntu", Read((".disk/info", "Ubuntu"), (".treeinfo", "[general]\nname = Fedora\n")));

    [Fact]
    public void WithoutAnyMarker_ThereIsNoReleaseInfo() =>
        Assert.Null(Read(("readme.txt", "hello")));

    [Fact]
    public void TreeInfoWithoutGeneralNames_IsIgnored() =>
        Assert.Null(Read((".treeinfo", "[tree]\narch = x86_64\n")));
}
