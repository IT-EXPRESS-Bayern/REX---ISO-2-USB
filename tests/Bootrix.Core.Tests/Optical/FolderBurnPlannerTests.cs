// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Optical;

namespace Bootrix.Core.Tests.Optical;

public sealed class FolderBurnPlannerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bootrix-folder-" + Guid.NewGuid().ToString("N"));

    public FolderBurnPlannerTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static OpticalMedia Dvd(long freeSectors = 2_295_104, OpticalMediaType type = OpticalMediaType.DvdPlusR) => new()
    {
        Type = type,
        State = OpticalMediaState.Blank,
        IsSupported = true,
        FreeSectors = freeSectors,
        TotalSectors = freeSectors,
    };

    private string Folder(params (string Path, int Size)[] files)
    {
        var root = Path.Combine(_dir, "src");
        foreach (var (path, size) in files)
        {
            var full = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, new byte[size]);
        }

        Directory.CreateDirectory(root);
        return root;
    }

    private static FolderScan FakeScan(long largestFile = 1000, int depth = 2, int longestName = 20, bool windows = false) =>
        new(largestFile, 10, 3, 2, depth, longestName, Path.Combine("data", "big.wim"), largestFile, windows);

    [Fact]
    public void ScanCountsFilesDirectoriesAndSize()
    {
        var root = Folder(("a.txt", 100), ("sub/b.bin", 5000), ("sub/deep/c.bin", 2048));

        var scan = FolderBurnPlanner.Scan(root);

        Assert.Equal(3, scan.FileCount);
        Assert.Equal(2, scan.DirectoryCount);
        Assert.Equal(7148, scan.TotalBytes);
        Assert.Equal(1 + 3 + 1, scan.PayloadSectors);
        Assert.Equal(2, scan.MaxDepth);
        Assert.Equal(5000, scan.LargestFileBytes);
        Assert.EndsWith("b.bin", scan.LargestFile);
        Assert.False(scan.LooksLikeWindowsSetup);
    }

    [Fact]
    public void ScanRecognisesAWindowsSetupTree()
    {
        var root = Folder(("sources/install.wim", 10), ("bootmgr", 10), ("efi/boot/bootx64.efi", 10));

        Assert.True(FolderBurnPlanner.Scan(root).LooksLikeWindowsSetup);
    }

    [Fact]
    public void ImageWithoutBootFilesIsNotWindowsSetup()
    {
        var root = Folder(("sources/install.wim", 10));

        Assert.False(FolderBurnPlanner.Scan(root).LooksLikeWindowsSetup);
    }

    [Fact]
    public void MissingFolderIsReported()
    {
        var ex = Assert.Throws<BootrixException>(() => FolderBurnPlanner.Scan(Path.Combine(_dir, "nope")));

        Assert.Equal(ErrorCode.ImageUnreadable, ex.Code);
    }

    [Fact]
    public void OrdinaryFolderGetsAllThreeFileSystemsOnDvd()
    {
        var plan = FolderBurnPlanner.Plan(FakeScan(), new FolderBurnRequest { SourceFolder = "/x/photos" }, Dvd(), null);

        Assert.Equal(DiscFileSystems.Iso9660 | DiscFileSystems.Joliet | DiscFileSystems.Udf, plan.FileSystems);
        Assert.Equal(UdfRevision.Udf102, plan.UdfRevision);
        Assert.Empty(plan.Warnings);
        Assert.Equal("photos", plan.VolumeLabel);
    }

    [Fact]
    public void BluRayGetsUdf250()
    {
        var plan = FolderBurnPlanner.Plan(FakeScan(), new FolderBurnRequest { SourceFolder = "/x/photos" }, Dvd(type: OpticalMediaType.BdR), null);

        Assert.Equal(DiscFileSystems.Udf, plan.FileSystems);
        Assert.Equal(UdfRevision.Udf250, plan.UdfRevision);
    }

    [Fact]
    public void ExplicitChoiceIsKept()
    {
        var request = new FolderBurnRequest { SourceFolder = "/x/y", FileSystems = DiscFileSystems.Joliet, UdfRevision = UdfRevision.Udf201 };

        var plan = FolderBurnPlanner.Plan(FakeScan(), request, Dvd(), null);

        Assert.Equal(DiscFileSystems.Joliet, plan.FileSystems);
        Assert.Equal(UdfRevision.Udf201, plan.UdfRevision);
    }

    [Fact]
    public void LargeFileForcesUdfOnlyOnOrdinaryData()
    {
        var plan = FolderBurnPlanner.Plan(FakeScan(largestFile: 5L << 30), new FolderBurnRequest { SourceFolder = "/x/backup" }, Dvd(type: OpticalMediaType.BdR, freeSectors: 12_000_000), null);

        Assert.Equal(DiscFileSystems.Udf, plan.FileSystems);
        Assert.Contains(FolderBurnWarning.UdfOnlyForLargeFile, plan.Warnings);
    }

    [Fact]
    public void LargeFileWithoutUdfCannotBeBurned()
    {
        var request = new FolderBurnRequest { SourceFolder = "/x/backup", FileSystems = DiscFileSystems.Iso9660 | DiscFileSystems.Joliet };

        var ex = Assert.Throws<BootrixException>(() => FolderBurnPlanner.Plan(FakeScan(largestFile: 5L << 30), request, Dvd(freeSectors: 12_000_000), null));

        Assert.Equal(ErrorCode.DiscFileTooLarge, ex.Code);
    }

    [Fact]
    public void WindowsSetupWithLargeInstallImageNeedsTheRemaster()
    {
        var ex = Assert.Throws<BootrixException>(() =>
            FolderBurnPlanner.Plan(FakeScan(largestFile: 6L << 30, windows: true), new FolderBurnRequest { SourceFolder = "/x/win" }, Dvd(freeSectors: 12_000_000), null));

        Assert.Equal(ErrorCode.DiscFileTooLarge, ex.Code);
        Assert.Equal("big.wim", ex.Arguments[0]);
    }

    [Fact]
    public void FileOfExactlyFourGigabytesMinusOneStillFitsIso9660()
    {
        var plan = FolderBurnPlanner.Plan(FakeScan(largestFile: uint.MaxValue), new FolderBurnRequest { SourceFolder = "/x/a" }, Dvd(freeSectors: 12_000_000), null);

        Assert.Equal(DiscFileSystems.Iso9660 | DiscFileSystems.Joliet | DiscFileSystems.Udf, plan.FileSystems);
    }

    [Fact]
    public void DeepTreeDropsIso9660()
    {
        var plan = FolderBurnPlanner.Plan(FakeScan(depth: 11), new FolderBurnRequest { SourceFolder = "/x/a" }, Dvd(), null);

        Assert.Equal(DiscFileSystems.Joliet | DiscFileSystems.Udf, plan.FileSystems);
        Assert.Contains(FolderBurnWarning.Iso9660Dropped, plan.Warnings);
    }

    [Fact]
    public void LongNamesDropJoliet()
    {
        var plan = FolderBurnPlanner.Plan(FakeScan(longestName: 120), new FolderBurnRequest { SourceFolder = "/x/a" }, Dvd(), null);

        Assert.Equal(DiscFileSystems.Iso9660 | DiscFileSystems.Udf, plan.FileSystems);
        Assert.Contains(FolderBurnWarning.JolietDropped, plan.Warnings);
    }

    [Fact]
    public void RestrictionsNeverRemoveTheOnlyRequestedFileSystem()
    {
        var request = new FolderBurnRequest { SourceFolder = "/x/a", FileSystems = DiscFileSystems.Iso9660 };

        var plan = FolderBurnPlanner.Plan(FakeScan(depth: 20), request, Dvd(), null);

        Assert.Equal(DiscFileSystems.Iso9660, plan.FileSystems);
    }

    [Fact]
    public void FolderLargerThanTheDiscIsRejected()
    {
        var scan = FakeScan() with { PayloadSectors = 3_000_000 };

        var ex = Assert.Throws<BootrixException>(() => FolderBurnPlanner.Plan(scan, new FolderBurnRequest { SourceFolder = "/x/a" }, Dvd(), null));

        Assert.Equal(ErrorCode.DeviceTooSmall, ex.Code);
    }

    [Theory]
    [InlineData("Backup 2024", DiscFileSystems.Udf, "Backup 2024")]
    [InlineData("Fotos/Urlaub:2024?", DiscFileSystems.Udf, "Fotos_Urlaub_2024_")]
    [InlineData("A very long volume label that exceeds thirty-two", DiscFileSystems.Iso9660 | DiscFileSystems.Udf, "A very long volume label that ex")]
    [InlineData("A very long volume label", DiscFileSystems.Joliet | DiscFileSystems.Udf, "A very long volu")]
    [InlineData("Größe", DiscFileSystems.Udf, "Gr__e")]
    [InlineData("   ", DiscFileSystems.Udf, "fallback")]
    [InlineData(null, DiscFileSystems.Udf, "fallback")]
    [InlineData("***", DiscFileSystems.Udf, "___")]
    public void LabelsAreMadeAcceptableToImapi(string? label, DiscFileSystems systems, string expected)
    {
        Assert.Equal(expected, FolderBurnPlanner.NormalizeLabel(label, systems, "fallback"));
    }

    [Fact]
    public void EmptyFallbackBecomesDisc()
    {
        Assert.Equal("DISC", FolderBurnPlanner.NormalizeLabel(null, DiscFileSystems.Udf, ""));
    }
}
