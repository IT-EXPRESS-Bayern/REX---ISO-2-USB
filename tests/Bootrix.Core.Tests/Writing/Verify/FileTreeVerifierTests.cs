// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Tests.Images.Support;
using Bootrix.Core.Writing.Verify;

namespace Bootrix.Core.Tests.Writing.Verify;

/// <summary>The file-mode check against ISOs made by xorriso; the "medium" is a copy of the folder the ISO was built from.</summary>
public sealed class FileTreeVerifierTests : IDisposable
{
    private readonly TestDirectory _dir = new("bootrix-tree");

    public void Dispose() => _dir.Dispose();

    private static Dictionary<string, byte[]> Files() => new()
    {
        ["boot/syslinux.cfg"] = "default live\nlabel live\n  append boot=live\n"u8.ToArray(),
        ["live/filesystem.squashfs"] = TestDirectory.Compressible(700_000),
        ["sources/install.wim"] = TestDirectory.Compressible(300_000, seed: 3),
        ["README.txt"] = "hello"u8.ToArray(),
        ["EFI/BOOT/BOOTX64.EFI"] = TestDirectory.Compressible(20_000, seed: 5),
    };

    /// <returns>The ISO and a folder holding exactly its files.</returns>
    private (string Iso, string Medium) Build()
    {
        var iso = IsoBuilder.Build(_dir, "tree", Files());
        var medium = _dir.File("medium");
        CopyTree(Path.Combine(_dir.Path, "tree-tree"), medium);
        return (iso, medium);
    }

    private static void CopyTree(string source, string target)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }
    }

    private static FileTreeReport Compare(string iso, string medium)
    {
        using var stream = File.OpenRead(iso);
        return FileTreeVerifier.Compare(stream, medium);
    }

    [ToolFact("xorriso")]
    public void IdenticalFiles_Match()
    {
        var (iso, medium) = Build();

        var report = Compare(iso, medium);

        Assert.True(report.Matches);
        Assert.Empty(report.Differences);
        Assert.Equal(5, report.FilesCompared);
        Assert.Null(report.ToException("stick"));
    }

    [ToolFact("xorriso")]
    public void ExtraFilesOnTheMedium_AreIgnored()
    {
        var (iso, medium) = Build();
        Directory.CreateDirectory(Path.Combine(medium, "System Volume Information"));
        File.WriteAllText(Path.Combine(medium, "System Volume Information", "x"), "x");
        File.WriteAllText(Path.Combine(medium, "bootmgr.extra"), "added by the writer");

        Assert.True(Compare(iso, medium).Matches);
    }

    [ToolFact("xorriso")]
    public void CaseDoesNotMatter()
    {
        var (iso, medium) = Build();
        Directory.Move(Path.Combine(medium, "live"), Path.Combine(medium, "LIVE"));
        File.Move(Path.Combine(medium, "LIVE", "filesystem.squashfs"), Path.Combine(medium, "LIVE", "FILESYSTEM.SQUASHFS"));

        Assert.True(Compare(iso, medium).Matches);
    }

    [ToolFact("xorriso")]
    public void ChangedContent_MissingAndShortFiles_AreNamed()
    {
        var (iso, medium) = Build();
        var squash = Path.Combine(medium, "live", "filesystem.squashfs");
        var bytes = File.ReadAllBytes(squash);
        bytes[123_456] ^= 0x10;
        File.WriteAllBytes(squash, bytes);
        File.Delete(Path.Combine(medium, "README.txt"));
        File.WriteAllBytes(Path.Combine(medium, "EFI", "BOOT", "BOOTX64.EFI"), new byte[100]);

        var report = Compare(iso, medium);

        Assert.False(report.Matches);
        var byPath = report.Differences.ToDictionary(d => d.Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(FileDifferenceKind.ContentDiffers, byPath["live/filesystem.squashfs"].Kind);
        Assert.Equal(FileDifferenceKind.Missing, byPath["readme.txt"].Kind);
        Assert.Equal(FileDifferenceKind.SizeDiffers, byPath["efi/boot/bootx64.efi"].Kind);
        Assert.Equal(100, byPath["efi/boot/bootx64.efi"].ActualBytes);
        var error = report.ToException("stick")!;
        Assert.Equal(ErrorCode.VerifyFilesDiffer, error.Code);
        Assert.Equal("3", error.Arguments[0]!.ToString());
    }

    [ToolFact("xorriso")]
    public void PatchedBootMenus_AreListedButNotCountedAsFailures()
    {
        var (iso, medium) = Build();
        File.WriteAllText(Path.Combine(medium, "boot", "syslinux.cfg"), "default live\nlabel live\n  append boot=live persistence\n");

        var report = Compare(iso, medium);

        Assert.True(report.Matches);
        var difference = Assert.Single(report.Differences);
        Assert.True(difference.LikelyPatched);
        Assert.Equal("boot/syslinux.cfg", difference.Path);
    }

    [ToolFact("xorriso")]
    public void ASplitInstallWim_IsReportedAndNotCountedAsMissing()
    {
        var (iso, medium) = Build();
        File.Delete(Path.Combine(medium, "sources", "install.wim"));
        File.WriteAllBytes(Path.Combine(medium, "sources", "install.swm"), new byte[1000]);

        var report = Compare(iso, medium);

        Assert.True(report.Matches);
        Assert.Equal(["sources/install.wim"], report.SplitFiles);
    }

    [Fact]
    public void ImageWithoutAFileTree_IsRefusedWithAHint()
    {
        var disk = new byte[1 << 20];
        disk[510] = 0x55;
        disk[511] = 0xAA;
        var medium = _dir.File("empty");
        Directory.CreateDirectory(medium);

        var error = Assert.Throws<BootrixException>(() => FileTreeVerifier.Compare(new MemoryStream(disk), medium));

        Assert.Equal(ErrorCode.ImageUnsupported, error.Code);
    }
}
