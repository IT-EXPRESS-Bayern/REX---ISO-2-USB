// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bootrix.Core.Images;
using Bootrix.Core.Images.Integrity;
using Bootrix.Core.Images.Iso;
using Bootrix.Core.Tests.Images.Support;

namespace Bootrix.Core.Tests.Images.Iso;

/// <summary>Pure UDF volumes (no ISO 9660 side), formatted by mkudffs and measured by udfinfo.</summary>
public sealed class UdfAnchorTests : IDisposable
{
    private const long MiB = 1024 * 1024;

    private readonly TestDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private string FormatUdf(long sizeBytes, int blockSize = 2048)
    {
        var path = DiskImageBuilder.Create(_dir, "volume.udf", sizeBytes);
        ReferenceTool.Run("mkudffs", "--media-type=hd", "--blocksize=" + blockSize.ToString(CultureInfo.InvariantCulture), "--lvid=BOOTRIXUDF", path);
        return path;
    }

    /// <summary>The "start=257, blocks=32248, type=PSPACE" line of udfinfo.</summary>
    private static (long Start, long Blocks) PartitionSpace(string path)
    {
        var line = ReferenceTool.Run("udfinfo", path).StandardOutput
            .Split('\n')
            .First(l => l.EndsWith("type=PSPACE", StringComparison.Ordinal));
        var fields = line.Split(',').Select(f => f.Trim().Split('=')).ToDictionary(p => p[0], p => p[1]);
        return (long.Parse(fields["start"], CultureInfo.InvariantCulture), long.Parse(fields["blocks"], CultureInfo.InvariantCulture));
    }

    [ToolTheory("mkudffs", "udfinfo")]
    [InlineData(16, 2048)]
    [InlineData(64, 2048)]
    [InlineData(33, 512)]
    [InlineData(40, 4096)]
    public void Read_AgreesWithUdfinfoOnThePartitionSpace(int megabytes, int blockSize)
    {
        var path = FormatUdf(megabytes * MiB, blockSize);
        var (start, blocks) = PartitionSpace(path);

        using var stream = File.OpenRead(path);
        var volume = UdfAnchor.Read(stream);

        Assert.NotNull(volume);
        Assert.Equal(blockSize, volume.BlockSize);
        Assert.Equal(start, volume.PartitionStartBlock);
        Assert.Equal(blocks, volume.PartitionBlocks);
        Assert.Equal((start + blocks) * blockSize, volume.ExpectedBytes);
    }

    [Fact]
    public void Read_DataWithoutAnAnchor_IsNull()
    {
        using var stream = new MemoryStream(new byte[2 * MiB]);

        Assert.Null(UdfAnchor.Read(stream));
    }

    [Fact]
    public void Read_ImageShorterThanTheAnchorSector_IsNull()
    {
        using var stream = new MemoryStream(new byte[100_000]);

        Assert.Null(UdfAnchor.Read(stream));
    }

    [ToolFact("mkudffs", "udfinfo")]
    public void Read_AnchorWithBrokenTagChecksum_IsNull()
    {
        var path = FormatUdf(16 * MiB);
        var data = File.ReadAllBytes(path);
        data[256 * 2048 + 4] ^= 0xFF;
        using var stream = new MemoryStream(data);

        Assert.Null(UdfAnchor.Read(stream));
    }

    [ToolFact("mkudffs", "udfinfo")]
    public async Task Inspect_PureUdfImage_IsAUdfContainerWithItsVolumeLabel()
    {
        var path = FormatUdf(16 * MiB);

        var result = await new ImageInspector().InspectAsync(path);

        Assert.Equal(ImageContainer.Udf, result.Container);
        Assert.Equal("BOOTRIXUDF", result.Profile.VolumeLabel);
        Assert.False(result.HasErrors);
    }

    [ToolFact("mkudffs", "udfinfo")]
    public void Check_IntactPureUdfImage_HasNoFindings()
    {
        var path = FormatUdf(16 * MiB);

        Assert.Empty(ImageIntegrityChecker.Check(path).Findings);
    }

    [ToolTheory("mkudffs", "udfinfo")]
    [InlineData(0.25)]
    [InlineData(0.6)]
    [InlineData(0.9)]
    public void Check_TruncatedPureUdfImage_IsReportedWithTheExpectedSize(double keep)
    {
        var path = FormatUdf(16 * MiB);
        var (start, blocks) = PartitionSpace(path);
        var cut = (long)(16 * MiB * keep);
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Write))
        {
            file.SetLength(cut);
        }

        var report = ImageIntegrityChecker.Check(path);

        // The anchors at the end are gone, but the one at sector 256 survives and still knows the full size.
        var finding = report.Findings.Single(f => f.Key == ImageWarningKeys.UdfTruncated);
        Assert.Equal(WarningSeverity.Error, finding.Severity);
        Assert.Equal((start + blocks) * 2048, finding.Arguments[0]);
        Assert.Equal(cut, finding.Arguments[1]);
    }
}
