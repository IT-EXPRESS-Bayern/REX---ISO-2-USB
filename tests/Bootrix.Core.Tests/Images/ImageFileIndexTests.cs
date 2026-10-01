// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images;

namespace Bootrix.Core.Tests.Images;

public sealed class ImageFileIndexTests
{
    private static ImageFileIndex Index(params string[] files)
    {
        var index = new ImageFileIndex();
        foreach (var file in files)
        {
            index.AddFile(file, file.Length);
        }

        return index;
    }

    [Theory]
    [InlineData(@"\EFI\BOOT\BOOTX64.EFI;1", "EFI/BOOT/BOOTX64.EFI")]
    [InlineData("/a/b/", "a/b")]
    [InlineData("BOOTMGR.", "BOOTMGR")]
    [InlineData("dir/FILE.TXT;12", "dir/FILE.TXT")]
    [InlineData("dir/semi;colon.txt", "dir/semi;colon.txt")]
    [InlineData("a;1/b", "a;1/b")]
    [InlineData("", "")]
    [InlineData("/", "")]
    public void Normalize_StripsVersionsSeparatorsAndTrailingDots(string path, string expected) =>
        Assert.Equal(expected, ImageFileIndex.Normalize(path));

    [Theory]
    [InlineData("efi/boot/bootx64.efi", "efi/*/bootx64.efi", true)]
    [InlineData("efi/boot/x/bootx64.efi", "efi/*/bootx64.efi", false)]
    [InlineData("efi/boot/x/bootx64.efi", "efi/**/bootx64.efi", true)]
    [InlineData("efi/bootx64.efi", "efi/**/bootx64.efi", true)]
    [InlineData("bootx64.efi", "**/bootx64.efi", true)]
    [InlineData("a/b/c.txt", "**", true)]
    [InlineData("a/b/c.txt", "a/**", true)]
    [InlineData("a/b", "a/*", true)]
    [InlineData("a/b/c.txt", "a/*", false)]
    [InlineData("abc", "a?c", true)]
    [InlineData("a/c", "a?c", false)]
    [InlineData("EFI/BOOT/BOOTX64.EFI", "efi/boot/*.efi", true)]
    [InlineData("boot/x/grub.cfg", "boot/*.cfg", false)]
    [InlineData("axb", "a.*", false)]
    [InlineData("a.b", "a.*", true)]
    [InlineData("casper/vmlinuz", "casper*", false)]
    [InlineData("casper_pop-os_22.04", "casper*pop-os*", true)]
    [InlineData("x(1)/y", "x(1)/*", true)]
    public void Glob_StarsStayInsideASegmentAndDoubleStarsSpanSegments(string path, string pattern, bool expected) =>
        Assert.Equal(expected, PathGlob.Get(pattern).IsMatch(ImageFileIndex.Normalize(path)));

    [Fact]
    public void Glob_WithoutWildcards_ComparesTheNormalisedPath()
    {
        var glob = PathGlob.Get(@"\EFI\BOOT\");

        Assert.Equal("EFI/BOOT", glob.Exact);
        Assert.True(glob.IsMatch("efi/boot"));
        Assert.False(glob.IsMatch("efi/boot/x"));
    }

    [Fact]
    public void Glob_IsCached() => Assert.Same(PathGlob.Get("a/**/b"), PathGlob.Get("a/**/b"));

    [Fact]
    public void AddFile_AlsoRegistersEveryParentDirectory()
    {
        var index = Index("a/b/c.txt");

        Assert.True(index.HasDirectory("a"));
        Assert.True(index.HasDirectory("A/B"));
        Assert.False(index.HasDirectory("a/b/c.txt"));
        Assert.True(index.HasFile("a/b/c.txt"));
        Assert.Equal(1, index.FileCount);
        Assert.Equal(2, index.DirectoryCount);
    }

    [Fact]
    public void Matches_CoversFilesAndDirectoriesWhileMatchesFileOnlyFiles()
    {
        var index = Index("isolinux/isolinux.bin", "boot/grub/i386-pc/normal.mod");

        Assert.True(index.Matches("boot/grub/i386-pc"));
        Assert.False(index.MatchesFile("boot/grub/i386-pc"));
        Assert.True(index.Matches("**/isolinux.bin"));
        Assert.True(index.MatchesFile("**/isolinux.bin"));
        Assert.False(index.Matches("**/syslinux.cfg"));
    }

    [Fact]
    public void Find_ReturnsNormalisedPaths()
    {
        var index = Index("EFI/BOOT/BOOTX64.EFI;1", "EFI/BOOT/GRUBX64.EFI;1", "EFI/BOOT/grub.cfg");

        Assert.Equal(["EFI/BOOT/BOOTX64.EFI", "EFI/BOOT/GRUBX64.EFI"], index.FindFiles("efi/**/*.efi").Order(StringComparer.Ordinal));
        Assert.Equal(["EFI/BOOT"], index.Find("efi/boot"));
    }

    [Fact]
    public void AddFile_DuplicateOfDifferentCase_IsCountedOnce()
    {
        var index = new ImageFileIndex();
        index.AddFile("Boot/KERNEL", 100);
        index.AddFile("boot/kernel", 5000);

        Assert.Equal(1, index.FileCount);
        Assert.Equal(100, index.TotalBytes);
        Assert.Equal(100, index.LengthOf("BOOT/kernel"));
    }

    [Fact]
    public void Totals_TrackSizeAndLargestFile()
    {
        var index = new ImageFileIndex();
        index.AddFile("a", 10);
        index.AddFile("b/c", 90);
        index.AddFile("d", 0);

        Assert.Equal(100, index.TotalBytes);
        Assert.Equal(90, index.LargestFileBytes);
    }

    [Fact]
    public void OriginalPathOf_ReturnsThePathTheReaderUsesToOpenTheFile()
    {
        var index = new ImageFileIndex();
        index.AddFile(@"\SOURCES\INSTALL.WIM;1", 7);

        Assert.Equal(@"\SOURCES\INSTALL.WIM;1", index.OriginalPathOf("sources/install.wim"));
        Assert.Null(index.OriginalPathOf("sources/boot.wim"));
    }

    [Fact]
    public void RootEntries_ListDirectoriesFirstThenFiles()
    {
        var index = Index("zeta.txt", "boot/a", "alpha.txt", "Efi/b");

        Assert.Equal(
            [("boot", true), ("Efi", true), ("alpha.txt", false), ("zeta.txt", false)],
            index.RootEntries().ToArray());
    }

    [Fact]
    public void Paths_WithoutNameAreIgnored()
    {
        var index = new ImageFileIndex();
        index.AddFile("/", 1);
        index.AddDirectory("");

        Assert.Equal(0, index.FileCount);
        Assert.Equal(0, index.DirectoryCount);
    }

    [Fact]
    public void FileWithoutExtensionOnLevel1Media_IsFoundUnderItsRealName()
    {
        var index = Index("BOOTMGR.");

        Assert.True(index.HasFile("bootmgr"));
        Assert.True(index.Matches("bootmgr"));
    }

    [Fact]
    public void DollarAndTildeInNames_AreLiteral()
    {
        var index = Index("$WIN_NT$.~BT/SETUPLDR.BIN");

        Assert.True(index.HasDirectory("$WIN_NT$.~BT"));
        Assert.True(index.Matches("$WIN_NT$.~BT"));
        Assert.True(index.Matches("$WIN_NT$.~BT/*.bin"));
    }
}
