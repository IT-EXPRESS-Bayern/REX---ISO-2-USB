// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Library;

namespace Bootrix.Core.Tests.Library;

public class LibraryFilesTests
{
    private static readonly string Sha = new string('0', 31) + new string('f', 33);

    [Theory]
    [InlineData("image.iso", "iso")]
    [InlineData("IMAGE.IMG", "img")]
    [InlineData("backup.tar.gz", "gz")]
    [InlineData("a.b.c.d.zip", "zip")]
    [InlineData("noextension", "bin")]
    [InlineData("trailingdot.", "bin")]
    [InlineData(".iso", "iso")]
    [InlineData("", "bin")]
    [InlineData(null, "bin")]
    [InlineData("x.i so", "bin")]
    [InlineData("x.ü", "bin")]
    [InlineData("x.verylongextension", "bin")]
    [InlineData("x.json", "bin")]
    [InlineData("x.JSON", "bin")]
    [InlineData("x.tmp", "bin")]
    [InlineData("C:\\images\\win.iso", "iso")]
    [InlineData("/var/images/linux.img", "img")]
    [InlineData("dir.with.dots\\file", "bin")]
    [InlineData("dir.with.dots/file", "bin")]
    [InlineData("..\\..\\x.iso", "iso")]
    public void ExtensionIsPlainOrBin(string? fileName, string expected)
    {
        Assert.Equal(expected, LibraryFiles.Extension(fileName));
    }

    [Fact]
    public void NamesAreBuiltFromTheHashAndTheSanitizedExtensionOnly()
    {
        Assert.Equal($"{Sha}.iso", LibraryFiles.ImageName(Sha, "x.ISO"));
        Assert.Equal($"{Sha}.json", LibraryFiles.MetadataName(Sha));
        Assert.True(LibraryFiles.IsImageNameFor($"{Sha}.iso", Sha));
        Assert.True(LibraryFiles.IsImageNameFor($"{Sha}.bin", Sha));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("abc", false)]
    [InlineData("0000000000000000000000000000000fffffffffffffffffffffffffffffffff.iso", true)]
    [InlineData("0000000000000000000000000000000fffffffffffffffffffffffffffffffff.ISO", false)]
    [InlineData("0000000000000000000000000000000fffffffffffffffffffffffffffffffff.iso\n", false)]
    [InlineData("0000000000000000000000000000000fffffffffffffffffffffffffffffffff", false)]
    [InlineData("0000000000000000000000000000000fffffffffffffffffffffffffffffffff.", false)]
    [InlineData("0000000000000000000000000000000fffffffffffffffffffffffffffffffff.json", false)]
    [InlineData("0000000000000000000000000000000fffffffffffffffffffffffffffffffff.tmp", false)]
    [InlineData("0000000000000000000000000000000fffffffffffffffffffffffffffffffff.iso.tmp", false)]
    [InlineData("0000000000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF.iso", false)]
    [InlineData("../0000000000000000000000000000000fffffffffffffffffffffffffffffffff.iso", false)]
    [InlineData("0000000000000000000000000000000fffffffffffffffffffffffffffffffff.toolongext", false)]
    public void OnlyTheLibrarysOwnNamesAreImageNames(string name, bool expected)
    {
        Assert.Equal(expected, LibraryFiles.IsImageName(name));
    }

    [Fact]
    public void AnImageNameMustBelongToTheHashItIsFor()
    {
        Assert.False(LibraryFiles.IsImageNameFor($"{new string('1', 64)}.iso", Sha));
        Assert.False(LibraryFiles.IsImageNameFor(null, Sha));
    }

    [Theory]
    [InlineData("0000000000000000000000000000000fffffffffffffffffffffffffffffffff.json", true)]
    [InlineData("0000000000000000000000000000000fffffffffffffffffffffffffffffffff.JSON", false)]
    [InlineData("settings.json", false)]
    [InlineData(".json", false)]
    [InlineData("0000000000000000000000000000000fffffffffffffffffffffffffffffffff.json\n", false)]
    public void OnlyAHashWithJsonIsAMetadataName(string name, bool expected)
    {
        Assert.Equal(expected, LibraryFiles.IsMetadataName(name));
    }

    [Fact]
    public void TemporaryNamesAreRecognizedBySharedPrefixesOnly()
    {
        Assert.True(LibraryFiles.IsTemporaryName(LibraryFiles.TemporaryName($"{Sha}.json")));
        Assert.True(LibraryFiles.IsTemporaryName(LibraryFiles.TemporaryName($"{Sha}.iso")));
        Assert.True(LibraryFiles.IsTemporaryName(LibraryFiles.IncomingName()));
        Assert.False(LibraryFiles.IsTemporaryName("download.tmp"));
        Assert.False(LibraryFiles.IsTemporaryName($"{Sha}.iso"));
        Assert.False(LibraryFiles.IsTemporaryName(".tmp"));
        Assert.NotEqual(LibraryFiles.IncomingName(), LibraryFiles.IncomingName());
    }

    [Fact]
    public void ImageAndMetadataNamesNeverCollide()
    {
        foreach (var name in new[] { "x.json", "x.tmp", "x.JSON", "x.Tmp" })
        {
            var image = LibraryFiles.ImageName(Sha, name);

            Assert.NotEqual(LibraryFiles.MetadataName(Sha), image);
            Assert.False(LibraryFiles.IsTemporaryName(image));
            Assert.True(LibraryFiles.IsImageName(image));
        }
    }

    [Theory]
    [InlineData("https://downloads.example.org/a/b.iso?token=1&sig=2#x", "https://downloads.example.org/a/b.iso")]
    [InlineData("https://user:pass@example.org/f.iso", "https://example.org/f.iso")]
    [InlineData("http://example.org:8080/f.iso?x=1", "http://example.org:8080/f.iso")]
    [InlineData("file:///home/me/f.iso", "file:///home/me/f.iso")]
    [InlineData("a note, not an address", "a note, not an address")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void SourcesLoseQueryStringsFragmentsAndCredentials(string? source, string? expected)
    {
        Assert.Equal(expected, LibraryFiles.CleanSource(source));
    }

    [Fact]
    public void LongTextsAreCutAndControlCharactersDropped()
    {
        Assert.Equal(256, LibraryFiles.CleanText(new string('x', 1000))!.Length);
        Assert.Equal("ab", LibraryFiles.CleanText("a\u0000\u001b\tb"));
        Assert.Null(LibraryFiles.CleanText("\u0001\u0002"));
        Assert.Equal(512, LibraryFiles.CleanSource("https://example.org/" + new string('p', 2000))!.Length);
    }
}
