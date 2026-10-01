// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Bootrix.Core.Errors;
using Bootrix.Core.Images.Udif;

namespace Bootrix.Core.Tests.Images.Udif;

public class DmgChecksumTests
{
    [Theory]
    [InlineData("raw")]
    [InlineData("zlib")]
    [InlineData("mixed")]
    public void VerifyChecksums_OnIntactImage_ReportsEverythingValid(string codec)
    {
        using var reader = DmgFixtures.Open(DmgFixtures.Build(codec));

        var report = reader.VerifyChecksums();

        Assert.True(report.IsValid);
        Assert.Equal(DmgChecksumStatus.Valid, report.DataFork.Status);
        Assert.Equal(DmgChecksumStatus.Valid, report.Master.Status);
        Assert.All(report.Partitions, p => Assert.Equal(DmgChecksumStatus.Valid, p.Result.Status));
        Assert.Equal(DmgFixtures.Partitions.Count, report.Partitions.Count);
    }

    [Fact]
    public void VerifyChecksums_PartitionCrcExcludesIgnoredChunks()
    {
        // A partition made only of free space has no data in its checksum, which is the CRC of nothing.
        var image = new UdifBuilder().AddPartition(-1, "free (Apple_Free : 1)", 0, [ChunkSpec.Ignore(100)]).Build();

        using var reader = DmgFixtures.Open(image);

        var result = Assert.Single(reader.VerifyChecksums().Partitions).Result;
        Assert.Equal(DmgChecksumStatus.Valid, result.Status);
        Assert.Equal(0u, result.Expected);
    }

    [Fact]
    public void VerifyChecksums_DamagedPayload_ReportsDataForkAndPartitionMismatch()
    {
        var image = DmgFixtures.Build("raw");
        image[40 * 512 + 3000] ^= 0xFF;

        using var reader = DmgFixtures.Open(image);
        var report = reader.VerifyChecksums();

        Assert.False(report.IsValid);
        Assert.Equal(DmgChecksumStatus.Mismatch, report.DataFork.Status);
        Assert.Contains(report.Partitions, p => p.Result.Status == DmgChecksumStatus.Mismatch);
        Assert.Equal(ErrorCode.ImageHashMismatch, Assert.Throws<BootrixException>(report.ThrowIfInvalid).Code);
    }

    [Fact]
    public void VerifyChecksums_WrongMasterChecksum_IsReported()
    {
        var image = DmgFixtures.Build("zlib", template: new UdifBuilder { MutateTrailer = t => t[0x160 + 8 + 3] ^= 0x01 });

        using var reader = DmgFixtures.Open(image);
        var report = reader.VerifyChecksums();

        Assert.Equal(DmgChecksumStatus.Mismatch, report.Master.Status);
        Assert.Equal(DmgChecksumStatus.Valid, report.DataFork.Status);
    }

    [Fact]
    public void VerifyChecksums_WithoutChecksums_ReportsNotPresent()
    {
        using var reader = DmgFixtures.Open(DmgFixtures.Build("zlib", template: new UdifBuilder { Checksums = false }));

        var report = reader.VerifyChecksums();

        Assert.True(report.IsValid);
        Assert.Equal(DmgChecksumStatus.NotPresent, report.DataFork.Status);
        Assert.Equal(DmgChecksumStatus.NotPresent, report.Master.Status);
        Assert.All(report.Partitions, p => Assert.Equal(DmgChecksumStatus.NotPresent, p.Result.Status));
    }

    [Fact]
    public void VerifyChecksums_OtherChecksumType_IsReportedAsUnsupported()
    {
        var image = DmgFixtures.Build("raw", template: new UdifBuilder
        {
            MutateTrailer = t => BinaryPrimitives.WriteUInt32BigEndian(t.AsSpan(0x50), 1),
        });

        using var reader = DmgFixtures.Open(image);

        Assert.Equal(DmgChecksumStatus.Unsupported, reader.VerifyChecksums().DataFork.Status);
    }

    [Fact]
    public void VerifyChecksums_LongZeroRun_IsFoldedIntoTheCrcWithoutDecoding()
    {
        // 96 MiB of zeros: the builder hashes them byte by byte, the reader must reach the same value in logarithmic time.
        var head = ImageTestData.Text(8 * 512, 31);
        var image = new UdifBuilder().AddPartition(
            0, "disk image (Apple_HFS : 1)", 0, [ChunkSpec.Raw(head), ChunkSpec.Zero(96 * 2048), ChunkSpec.Zlib(head)]).Build();

        using var reader = DmgFixtures.Open(image);
        var report = reader.VerifyChecksums();

        Assert.Equal(DmgChecksumStatus.Valid, report.Partitions[0].Result.Status);
    }
}
