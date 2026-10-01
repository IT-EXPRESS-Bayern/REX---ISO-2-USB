// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Bootrix.Core.Images;
using Bootrix.Core.Images.Compression;
using Bootrix.Core.Tests.Images.Support;

namespace Bootrix.Core.Tests.Images;

public sealed class ImageInspectorCompressedTests : IDisposable
{
    private const long MiB = 1024 * 1024;

    private readonly TestDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private string UbuntuIso()
    {
        var efi = IsoBuilder.FatImage(_dir, "efi.img", 2048, new Dictionary<string, string> { ["EFI/BOOT/BOOTX64.EFI"] = "efi" });
        var tree = new Dictionary<string, byte[]>
        {
            [".disk/info"] = Encoding.UTF8.GetBytes("Ubuntu 24.04 LTS \"Noble Numbat\" - Release amd64"),
            ["casper/vmlinuz"] = new byte[200_000],
            ["isolinux/isolinux.bin"] = IsoBuilder.FakeBootLoader(),
            ["boot/efi.img"] = efi,
        };
        return IsoBuilder.Build(_dir, "ubuntu", tree, new IsoOptions
        {
            Label = "UBUNTU",
            Hybrid = true,
            Gpt = true,
            BootImages = [new BootImageSpec("isolinux/isolinux.bin"), new BootImageSpec("boot/efi.img", Efi: true, LoadSize: 4096)],
        });
    }

    private string Compress(string tool, string[] arguments, string source, string target)
    {
        ReferenceTool.Run(tool, arguments, _dir.Path, source, target);
        return target;
    }

    [ToolFact("xorriso", "mkfs.vfat", "mcopy", "xz")]
    public async Task Inspect_XzCompressedIso_IsAnalysedCompletelyWithExactSize()
    {
        var iso = UbuntuIso();
        var xz = Compress("xz", ["-c", "-1"], iso, _dir.File("ubuntu.iso.xz"));

        var result = await new ImageInspector().InspectAsync(xz);

        Assert.Equal(CompressionFormat.Xz, result.Compression);
        Assert.Equal(ImageContainer.Iso9660, result.Container);
        Assert.Equal(new FileInfo(iso).Length, result.ImageLength);
        Assert.Equal(new FileInfo(xz).Length, result.FileLength);
        Assert.Equal("ubuntu", result.Profile.Family);
        Assert.Equal(ImageKind.LinuxHybrid, result.Profile.Kind);
        Assert.True(result.Profile.IsCompressed);
        Assert.Equal(new FileInfo(iso).Length, result.Profile.ImageBytes);
        Assert.False(result.IsPartial);
        Assert.False(result.IsTruncated);
        Assert.True(result.Profile.HasEspPartition);
    }

    [ToolFact("xorriso", "mkfs.vfat", "mcopy", "gzip")]
    public async Task Inspect_GzipIsoThatFitsTheDecodedPrefix_HasAKnownLength()
    {
        var iso = UbuntuIso();
        var gz = Compress("gzip", ["-c"], iso, _dir.File("ubuntu.iso.gz"));

        var result = await new ImageInspector().InspectAsync(gz);

        Assert.Equal(CompressionFormat.GZip, result.Compression);
        Assert.Equal(new FileInfo(iso).Length, result.ImageLength);
        Assert.Equal(new FileInfo(iso).Length, result.ImageLengthHint);
        Assert.Equal("ubuntu", result.Profile.Family);
        Assert.DoesNotContain(result.Warnings, warning => warning.Key == ImageWarningKeys.CompressedSizeUnknown);
    }

    [ToolFact("xorriso", "mkfs.vfat", "mcopy", "gzip")]
    public async Task Inspect_GzipIsoBeyondThePrefix_ReportsPartialAnalysisAndUnknownSize()
    {
        var iso = UbuntuIso();
        var gz = Compress("gzip", ["-c"], iso, _dir.File("big.iso.gz"));

        var result = await new ImageInspector().InspectAsync(gz, new ImageInspectOptions { MaxDecodedPrefixBytes = 1024 * 1024 });

        Assert.True(result.IsPartial);
        Assert.Null(result.ImageLength);
        Assert.Equal(new FileInfo(iso).Length, result.ImageLengthHint);
        Assert.Equal(0, result.Profile.ImageBytes);
        Assert.Contains(result.Warnings, warning => warning.Key == ImageWarningKeys.CompressedSizeUnknown);
        Assert.Contains(result.Warnings, warning => warning.Key == ImageWarningKeys.AnalysisPartial);
        Assert.Equal(ImageContainer.Iso9660, result.Container);
        Assert.True(result.Profile.IsHybrid);
        Assert.True(result.Profile.HasElToritoBios);
        Assert.True(result.Profile.HasElToritoEfi);
        Assert.Equal(0, result.FileCount);
    }

    [ToolFact("sfdisk", "mkfs.vfat", "mcopy", "xz")]
    public async Task Inspect_XzRawDisk_ReadsTheBootPartitionNearTheStart()
    {
        var fat = IsoBuilder.FatImage(_dir, "pi-fat.img", 8 * 1024, new Dictionary<string, string>
        {
            ["cmdline.txt"] = "root=/dev/mmcblk0p2",
            ["config.txt"] = "dtparam=audio=on",
            ["start4.elf"] = "fw",
        });
        var disk = DiskImageBuilder.CreateMbr(_dir, "pi.img", 96 * MiB,
            new MbrPartitionSpec(2048, fat.Length / 512, "c", Bootable: true), new MbrPartitionSpec(40_000, 100_000, "83"));
        DiskImageBuilder.WriteAt(disk, 2048 * 512, fat);
        var xz = Compress("xz", ["-c", "-1"], disk, _dir.File("pi.img.xz"));

        var result = await new ImageInspector().InspectAsync(xz, new ImageInspectOptions { MaxDecodedPrefixBytes = 4 * 1024 * 1024 });

        Assert.True(result.IsPartial);
        Assert.Equal(96 * MiB, result.ImageLength);
        Assert.Equal(96 * MiB, result.Profile.ImageBytes);
        Assert.Equal("raspberrypi", result.Profile.Family);
        Assert.Equal(ImageKind.RawDisk, result.Profile.Kind);
        Assert.False(result.IsTruncated);
    }

    [ToolFact("xorriso", "mkfs.vfat", "mcopy", "zip")]
    public async Task Inspect_ZipWithImageAndChecksum_InspectsTheImageEntry()
    {
        var iso = UbuntuIso();
        var folder = Path.Combine(_dir.Path, "zipcontent");
        Directory.CreateDirectory(folder);
        File.Copy(iso, Path.Combine(folder, "ubuntu.iso"));
        File.WriteAllText(Path.Combine(folder, "SHA256SUMS"), new string('0', 64) + " ubuntu.iso\n");
        var zip = _dir.File("download.zip");
        ReferenceTool.Run("zip", ["-q", "-j", zip, Path.Combine(folder, "ubuntu.iso"), Path.Combine(folder, "SHA256SUMS")], null, null, null);

        var result = await new ImageInspector().InspectAsync(zip);

        Assert.Equal(CompressionFormat.Zip, result.Compression);
        Assert.Equal("ubuntu.iso", result.ArchiveEntry);
        Assert.Equal(2, result.ArchiveEntries.Count);
        Assert.Equal("ubuntu", result.Profile.Family);
        Assert.Equal(new FileInfo(iso).Length, result.ImageLength);

        var other = await new ImageInspector().InspectAsync(zip, new ImageInspectOptions { ArchiveEntry = "SHA256SUMS" });
        Assert.Equal("SHA256SUMS", other.ArchiveEntry);
        Assert.Equal(ImageContainer.Unknown, other.Container);
    }

    [ToolFact("xorriso", "mkfs.vfat", "mcopy", "xz")]
    public async Task Inspect_TruncatedXz_IsReportedAsIncomplete()
    {
        var iso = UbuntuIso();
        var xz = Compress("xz", ["-c", "-1"], iso, _dir.File("cut.iso.xz"));
        using (var file = new FileStream(xz, FileMode.Open, FileAccess.Write))
        {
            file.SetLength(file.Length * 6 / 10);
        }

        var result = await new ImageInspector().InspectAsync(xz);

        Assert.True(result.IsTruncated);
        Assert.Contains(result.Warnings, warning => warning is { Key: ImageWarningKeys.CompressedIncomplete, Severity: WarningSeverity.Error });
    }

    [ToolFact("xorriso", "mkfs.vfat", "mcopy", "xz")]
    public async Task Inspect_XzOfATruncatedIso_FindsTheShortIsoThroughTheKnownLength()
    {
        var iso = UbuntuIso();
        var cut = _dir.File("cut.iso");
        File.WriteAllBytes(cut, File.ReadAllBytes(iso).AsSpan(0, (int)(new FileInfo(iso).Length * 6 / 10)).ToArray());
        var xz = Compress("xz", ["-c", "-1"], cut, _dir.File("cut.iso.xz"));

        var result = await new ImageInspector().InspectAsync(xz);

        Assert.True(result.IsTruncated);
        Assert.Contains(result.Warnings, warning => warning.Key == ImageWarningKeys.IsoTruncated);
    }

    [ToolFact("xorriso", "mkfs.vfat", "mcopy", "xz")]
    public async Task Inspect_DownloadInProgress_IsFlaggedByItsName()
    {
        var iso = UbuntuIso();
        var partial = _dir.File("ubuntu.iso.crdownload");
        File.Copy(iso, partial);

        var result = await new ImageInspector().InspectAsync(partial);

        Assert.True(result.IsTruncated);
        Assert.Contains(result.Warnings, warning => warning.Key == ImageWarningKeys.PartialDownload);
    }

    [Fact]
    public async Task Inspect_BmapNextToTheImage_IsReported()
    {
        var image = _dir.Write("sd.img", new byte[2048]);
        File.WriteAllText(_dir.File("sd.img.bmap"), "<bmap/>");

        var result = await new ImageInspector().InspectAsync(image);

        Assert.Equal(_dir.File("sd.img.bmap"), result.BmapPath);
    }

    [ToolFact("gzip")]
    public async Task Inspect_BmapForTheDecompressedName_IsFoundForACompressedImage()
    {
        var raw = _dir.Write("sd.img", new byte[8192]);
        var gz = Compress("gzip", ["-c"], raw, _dir.File("sd.img.gz"));
        File.Delete(raw);
        File.WriteAllText(_dir.File("sd.img.bmap"), "<bmap/>");

        var result = await new ImageInspector().InspectAsync(gz);

        Assert.Equal(_dir.File("sd.img.bmap"), result.BmapPath);
    }

    [ToolTheory("zstd", "bzip2", "xz", "compress")]
    [InlineData("zstd", "zst", CompressionFormat.Zstd)]
    [InlineData("bzip2", "bz2", CompressionFormat.BZip2)]
    [InlineData("compress", "Z", CompressionFormat.Compress)]
    public async Task Inspect_OtherCompressionFormats_AreUnpackedForTheAnalysis(string tool, string extension, CompressionFormat expected)
    {
        var floppy = IsoBuilder.FatImage(_dir, "boot.img", 1440, new Dictionary<string, string> { ["KERNEL.SYS"] = "k", ["COMMAND.COM"] = "c" });
        var raw = _dir.Write("boot.img", floppy);
        var compressed = Compress(tool, tool == "zstd" ? ["-c", "-q"] : ["-c"], raw, _dir.File("boot.img." + extension));

        var result = await new ImageInspector().InspectAsync(compressed);

        Assert.Equal(expected, result.Compression);
        Assert.Equal(ImageContainer.FatVolume, result.Container);
        Assert.Equal("freedos", result.Profile.Family);
        Assert.Equal(1440 * 1024, result.ImageLength);
    }

    [ToolFact("xz")]
    public async Task Inspect_LzmaAlone_IsRecognisedByContent()
    {
        var raw = _dir.Write("data.bin", new byte[100_000]);
        var lzma = Compress("xz", ["-c", "--format=lzma"], raw, _dir.File("renamed.dat"));

        var result = await new ImageInspector().InspectAsync(lzma);

        Assert.Equal(CompressionFormat.Lzma, result.Compression);
        Assert.Equal(100_000, result.ImageLength);
    }
}
