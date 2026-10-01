// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Images;
using Bootrix.Core.Images.Integrity;
using Bootrix.Core.Tests.Images.Support;

namespace Bootrix.Core.Tests.Images.Integrity;

public sealed class ImageIntegrityCheckerTests : IDisposable
{
    private const long MiB = 1024 * 1024;

    private readonly TestDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private static void Truncate(string path, long length)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Write);
        file.SetLength(length);
    }

    private static bool HasError(IntegrityReport report, string key) =>
        report.Findings.Any(finding => finding.Key == key && finding.Severity == WarningSeverity.Error);

    private string HybridIso(string name = "hybrid")
    {
        var efi = IsoBuilder.FatImage(_dir, "efi-" + name + ".img", 2048, new Dictionary<string, string> { ["EFI/BOOT/BOOTX64.EFI"] = "x" });
        return IsoBuilder.Build(_dir, name, new Dictionary<string, byte[]>
        {
            ["boot/efi.img"] = efi,
            ["isolinux/isolinux.bin"] = IsoBuilder.FakeBootLoader(),
            ["data.bin"] = new byte[1_000_000],
        },
        new IsoOptions
        {
            Hybrid = true,
            Gpt = true,
            BootImages = [new BootImageSpec("isolinux/isolinux.bin"), new BootImageSpec("boot/efi.img", Efi: true, LoadSize: 4096)],
        });
    }

    [ToolFact("xorriso", "mkfs.vfat", "mcopy")]
    public void Check_IntactHybridIso_HasNoFindings()
    {
        var report = ImageIntegrityChecker.Check(HybridIso());

        Assert.True(report.IsComplete);
        Assert.Empty(report.Findings);
    }

    [ToolTheory("xorriso", "mkfs.vfat", "mcopy")]
    [InlineData(0.3)]
    [InlineData(0.7)]
    [InlineData(0.99)]
    public void Check_TruncatedIso_ReportsExpectedAndActualSize(double keep)
    {
        var iso = HybridIso();
        var full = new FileInfo(iso).Length;
        Truncate(iso, (long)(full * keep));

        var report = ImageIntegrityChecker.Check(iso);

        Assert.False(report.IsComplete);
        var finding = report.Findings.Single(f => f.Key == ImageWarningKeys.IsoTruncated);
        Assert.Equal(full, finding.Arguments[0]);
        Assert.Equal((long)(full * keep), finding.Arguments[1]);
    }

    [ToolFact("xorriso", "mkfs.vfat", "mcopy")]
    public void Check_IsoLongerThanItsVolume_IsFine()
    {
        var iso = HybridIso();
        File.AppendAllText(iso, new string('\0', 4096));

        Assert.DoesNotContain(ImageIntegrityChecker.Check(iso).Findings, f => f.Key == ImageWarningKeys.IsoTruncated);
    }

    [ToolFact("xorriso", "mkfs.vfat", "mcopy")]
    public void Check_HybridIsoWithMissingBackupGpt_IsAWarningNotAnError()
    {
        var iso = HybridIso();
        var report = ImageIntegrityChecker.Check(iso);
        Assert.Empty(report.Findings);

        // Patch the ISO volume size down so that only the GPT backup is beyond the end after cutting.
        var full = new FileInfo(iso).Length;
        using (var file = new FileStream(iso, FileMode.Open, FileAccess.ReadWrite))
        {
            file.Position = 0x8050;
            file.Write(BitConverter.GetBytes(100u));
            file.Write(BitConverter.GetBytes(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(100u)));
            file.SetLength(full - 1024);
        }

        var cut = ImageIntegrityChecker.Check(iso);

        var backup = cut.Findings.Single(f => f.Key == ImageWarningKeys.GptBackupMissing);
        Assert.Equal(WarningSeverity.Warning, backup.Severity);
        Assert.True(cut.IsComplete);
    }

    [ToolFact("genisoimage")]
    public void Check_TruncatedUdfBridge_ReportsTheUdfPartitionAndTheIsoVolume()
    {
        var iso = BuildUdf();
        var full = new FileInfo(iso).Length;
        Truncate(iso, full / 2);

        var report = ImageIntegrityChecker.Check(iso);

        Assert.True(HasError(report, ImageWarningKeys.IsoTruncated));
        Assert.True(HasError(report, ImageWarningKeys.UdfTruncated));
    }

    [ToolFact("genisoimage")]
    public void Check_UdfPartitionLongerThanTheFile_IsFoundWithoutTheIsoSideAgreeing()
    {
        var iso = BuildUdf();
        var full = new FileInfo(iso).Length;

        // Shrink the claim of the ISO 9660 side so that only the UDF anchor knows the real size.
        using (var file = new FileStream(iso, FileMode.Open, FileAccess.ReadWrite))
        {
            file.Position = 0x8050;
            file.Write(BitConverter.GetBytes(100u));
            file.Write(BitConverter.GetBytes(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(100u)));
            file.SetLength(full * 3 / 5);
        }

        var report = ImageIntegrityChecker.Check(iso);

        Assert.False(HasError(report, ImageWarningKeys.IsoTruncated));
        Assert.True(HasError(report, ImageWarningKeys.UdfTruncated));
    }

    [ToolFact("genisoimage")]
    public void Check_IntactUdfBridge_HasNoFindings() => Assert.Empty(ImageIntegrityChecker.Check(BuildUdf()).Findings);

    private string BuildUdf()
    {
        var tree = Path.Combine(_dir.Path, "udf-tree");
        Directory.CreateDirectory(tree);
        File.WriteAllBytes(Path.Combine(tree, "payload.bin"), new byte[3_000_000]);
        var iso = _dir.File("bridge.iso");
        ReferenceTool.Run("genisoimage", ["-quiet", "-udf", "-J", "-V", "UDF", "-o", iso, tree], null, null, null);
        return iso;
    }

    [ToolFact("sfdisk")]
    public void Check_RawDiskCutThroughAPartition_ReportsThePartition()
    {
        var path = DiskImageBuilder.CreateMbr(_dir, "disk.img", 32 * MiB,
            new MbrPartitionSpec(2048, 20_000, "c"), new MbrPartitionSpec(30_000, 20_000, "83"));
        Truncate(path, 24_000 * 512);

        var report = ImageIntegrityChecker.Check(path);

        var finding = report.Findings.Single();
        Assert.Equal(ImageWarningKeys.PartitionBeyondEnd, finding.Key);
        Assert.Equal(2, finding.Arguments[0]);
        Assert.Equal(50_000L * 512, finding.Arguments[1]);
        Assert.Equal(24_000L * 512, finding.Arguments[2]);
        Assert.False(report.IsComplete);
    }

    [ToolFact("sfdisk")]
    public void Check_RawDiskWithAllPartitionsInside_IsComplete()
    {
        var path = DiskImageBuilder.CreateMbr(_dir, "ok.img", 32 * MiB, new MbrPartitionSpec(2048, 20_000, "c"));

        Assert.True(ImageIntegrityChecker.Check(path).IsComplete);
    }

    [ToolFact("sgdisk")]
    public void Check_GptDiskWithoutItsBackup_IsAnError()
    {
        var path = DiskImageBuilder.CreateGpt(_dir, "gpt.img", 32 * MiB, new GptPartitionSpec(2048, 4096, "8300", "root"));
        Assert.True(ImageIntegrityChecker.Check(path).IsComplete);

        Truncate(path, 16 * MiB);
        var report = ImageIntegrityChecker.Check(path);

        var finding = Assert.Single(report.Findings, f => f.Key == ImageWarningKeys.GptBackupMissing);
        Assert.Equal(WarningSeverity.Error, finding.Severity);
        Assert.Equal(32 * MiB, finding.Arguments[0]);
        Assert.Equal(16 * MiB, finding.Arguments[1]);
    }

    [ToolFact("wimcapture", "wiminfo")]
    public void Check_WimCutBeforeItsXml_IsTruncated()
    {
        var tree = Path.Combine(_dir.Path, "w");
        Directory.CreateDirectory(tree);
        File.WriteAllBytes(Path.Combine(tree, "a.bin"), new byte[500_000]);
        var wim = _dir.File("a.wim");
        ReferenceTool.Run("wimcapture", [tree, wim, "x", "x", "--compress=none"], null, null, null);
        Assert.Empty(ImageIntegrityChecker.Check(wim).Findings);

        var full = new FileInfo(wim).Length;
        Truncate(wim, full - 50);
        var report = ImageIntegrityChecker.Check(wim);

        var finding = Assert.Single(report.Findings);
        Assert.Equal(ImageWarningKeys.WimTruncated, finding.Key);
        Assert.Equal(full, finding.Arguments[0]);
    }

    [ToolFact("qemu-img")]
    public void Check_FixedAndDynamicVhd_NeedTheirFooter()
    {
        var raw = DiskImageBuilder.Create(_dir, "src.img", 4 * MiB);
        foreach (var subformat in new[] { "fixed", "dynamic" })
        {
            var vhd = _dir.File(subformat + ".vhd");
            ReferenceTool.Run("qemu-img", "convert", "-f", "raw", "-O", "vpc", "-o", "subformat=" + subformat, raw, vhd);
            Assert.Empty(ImageIntegrityChecker.Check(vhd).Findings);

            Truncate(vhd, new FileInfo(vhd).Length - 100);
            Assert.True(HasError(ImageIntegrityChecker.Check(vhd), ImageWarningKeys.VhdFooterMissing), subformat);
        }
    }

    private string Compressed(string tool, string[] arguments, string extension, byte[]? data = null)
    {
        var raw = _dir.Write("raw-" + extension, data ?? TestDirectory.Compressible(2 * 1024 * 1024));
        var target = _dir.File("data." + extension);
        ReferenceTool.Run(tool, arguments, null, raw, target);
        return target;
    }

    [ToolTheory("xz", "bzip2", "zstd")]
    [InlineData("xz", "xz")]
    [InlineData("bzip2", "bz2")]
    [InlineData("zstd", "zst")]
    public void Check_TruncatedCompressedFile_IsAnError(string tool, string extension)
    {
        var path = Compressed(tool, tool == "zstd" ? ["-c", "-q"] : ["-c"], extension);
        Assert.True(ImageIntegrityChecker.Check(path).IsComplete);

        Truncate(path, new FileInfo(path).Length * 9 / 10);
        var report = ImageIntegrityChecker.Check(path);

        Assert.True(HasError(report, ImageWarningKeys.CompressedIncomplete));
    }

    [ToolFact("zip")]
    public void Check_TruncatedZip_IsAnError()
    {
        var raw = _dir.Write("payload.img", TestDirectory.Compressible(1_000_000));
        var zip = _dir.File("a.zip");
        ReferenceTool.Run("zip", ["-q", "-j", zip, raw], null, null, null);
        Assert.True(ImageIntegrityChecker.Check(zip).IsComplete);

        Truncate(zip, new FileInfo(zip).Length / 2);

        Assert.True(HasError(ImageIntegrityChecker.Check(zip), ImageWarningKeys.CompressedIncomplete));
    }

    [ToolTheory("gzip", "compress")]
    [InlineData("gzip", "gz")]
    [InlineData("compress", "Z")]
    public void Check_FormatsWithoutTrailer_AreOnlyAnInfo(string tool, string extension)
    {
        var path = Compressed(tool, ["-c"], extension);

        var report = ImageIntegrityChecker.Check(path);

        var finding = Assert.Single(report.Findings);
        Assert.Equal(ImageWarningKeys.CompressedUnverified, finding.Key);
        Assert.Equal(WarningSeverity.Info, finding.Severity);
        Assert.True(report.IsComplete);
    }

    [ToolFact("xorriso", "mkfs.vfat", "mcopy", "xz")]
    public void Check_TruncatedIsoInsideXz_IsFoundThroughTheKnownDecodedSize()
    {
        var iso = HybridIso();
        var cut = _dir.File("cut.iso");
        File.WriteAllBytes(cut, File.ReadAllBytes(iso).AsSpan(0, (int)(new FileInfo(iso).Length / 2)).ToArray());
        var xz = _dir.File("cut.iso.xz");
        ReferenceTool.Run("xz", ["-c", "-1"], null, cut, xz);

        var report = ImageIntegrityChecker.Check(xz);

        Assert.True(HasError(report, ImageWarningKeys.IsoTruncated));
    }

    [ToolFact("gzip")]
    public async Task VerifyCompressedAsync_GzipWithTheWholeImage_ReturnsTheDecodedLength()
    {
        var data = TestDirectory.Compressible(3 * 1024 * 1024);
        var gz = Compressed("gzip", ["-c"], "gz", data);
        long reported = 0;

        var length = await ImageIntegrityChecker.VerifyCompressedAsync(gz, new Progress<long>(value => reported = Math.Max(reported, value)));

        Assert.Equal(data.Length, length);
        await Task.Delay(50);
        Assert.True(reported > 0);
    }

    [ToolFact("gzip")]
    public async Task VerifyCompressedAsync_TruncatedGzip_Fails()
    {
        var gz = Compressed("gzip", ["-c"], "gz");
        Truncate(gz, new FileInfo(gz).Length / 2);

        var ex = await Assert.ThrowsAsync<BootrixException>(() => ImageIntegrityChecker.VerifyCompressedAsync(gz));

        Assert.Equal(ErrorCode.ImageUnreadable, ex.Code);
    }

    [Theory]
    [InlineData("disk.iso.crdownload", true)]
    [InlineData("Unconfirmed 123456.crdownload", true)]
    [InlineData("ubuntu.iso.PART", true)]
    [InlineData("a.partial", true)]
    [InlineData("a.tmp", true)]
    [InlineData("a.iso.aria2", true)]
    [InlineData("a.iso.!ut", true)]
    [InlineData("a.iso", false)]
    [InlineData("partial.iso", false)]
    [InlineData("a.img.xz", false)]
    public void IsPartialDownloadName_RecognisesUnfinishedDownloads(string name, bool expected)
    {
        Assert.Equal(expected, ImageIntegrityChecker.IsPartialDownloadName(name));
        Assert.Equal(expected ? 1 : 0, ImageIntegrityChecker.CheckName(name).Count);
    }

    [Fact]
    public void Check_NonImageData_HasNoFindings()
    {
        var path = _dir.Write("noise.bin", new byte[100_000]);

        Assert.Empty(ImageIntegrityChecker.Check(path).Findings);
    }
}
