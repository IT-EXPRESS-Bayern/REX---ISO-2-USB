// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Bootrix.Core.Images.Udif;

namespace Bootrix.Core.Tests.Images.Udif;

public class DmgFormatVariantTests
{
    [Fact]
    public void TrailerInFront_IsFoundAndDecoded()
    {
        var image = DmgFixtures.Build("zlib", template: new UdifBuilder { TrailerAtFront = true });

        using var reader = DmgFixtures.Open(image);

        Assert.True(reader.Info.TrailerAtFront);
        Assert.Equal(DmgFixtures.Volume, DmgFixtures.ReadAll(reader));
        reader.VerifyChecksums().ThrowIfInvalid();
    }

    [Fact]
    public void TrailerBehindMacBinaryHeader_OffsetsAreRelativeToTheTrailer()
    {
        var image = DmgFixtures.Build("bzip2", template: new UdifBuilder { TrailerAtFront = true, Prefix = 128 });

        using var reader = DmgFixtures.Open(image);

        Assert.Equal(DmgFixtures.Volume, DmgFixtures.ReadAll(reader));
        reader.VerifyChecksums().ThrowIfInvalid();
    }

    [Fact]
    public void BlockTablesInResourceFork_AreReadWhenThePropertyListIsMissing()
    {
        var image = DmgFixtures.Build("mixed", template: new UdifBuilder { ResourceFork = true });

        using var reader = DmgFixtures.Open(image);

        Assert.True(reader.Info.UsesResourceFork);
        Assert.Equal(DmgFixtures.Partitions.Count, reader.Info.Partitions.Count);
        Assert.Equal("Apple_HFS (Apple_HFS : 3)", reader.Info.Partitions[3].Name);
        Assert.Equal(DmgFixtures.Volume, DmgFixtures.ReadAll(reader));
    }

    [Fact]
    public void CommentEntries_AreSkipped()
    {
        var image = DmgFixtures.Build("mixed", template: new UdifBuilder { Comments = true });

        using var reader = DmgFixtures.Open(image);

        Assert.Equal(DmgFixtures.Volume, DmgFixtures.ReadAll(reader));
        Assert.DoesNotContain(UdifChunkType.Comment, reader.Info.ChunkTypes);
        Assert.DoesNotContain(UdifChunkType.Terminator, reader.Info.ChunkTypes);
    }

    [Fact]
    public void UncompressedImageWithoutBlockTable_IsTheRawVolume()
    {
        var volume = ImageTestData.Volume(300, 9);
        var image = new byte[volume.Length + 512];
        volume.CopyTo(image, 0);
        var trailer = image.AsSpan(volume.Length);
        BinaryPrimitives.WriteUInt32BigEndian(trailer, 0x6B6F6C79);
        BinaryPrimitives.WriteUInt32BigEndian(trailer[4..], 4);
        BinaryPrimitives.WriteUInt32BigEndian(trailer[8..], 512);
        BinaryPrimitives.WriteUInt64BigEndian(trailer[0x20..], (ulong)volume.Length);
        BinaryPrimitives.WriteUInt64BigEndian(trailer[0x1EC..], 300);

        using var reader = DmgFixtures.Open(image);

        Assert.Equal(volume.Length, reader.Length);
        Assert.Equal(volume, DmgFixtures.ReadAll(reader));
    }

    [Fact]
    public void RawStub_ShorterThanItsSectorCount_IsReportedAsTruncated()
    {
        var image = new byte[512 * 10 + 512];
        var trailer = image.AsSpan(512 * 10);
        BinaryPrimitives.WriteUInt32BigEndian(trailer, 0x6B6F6C79);
        BinaryPrimitives.WriteUInt32BigEndian(trailer[4..], 4);
        BinaryPrimitives.WriteUInt32BigEndian(trailer[8..], 512);
        BinaryPrimitives.WriteUInt64BigEndian(trailer[0x1EC..], 4000);

        var ex = Assert.Throws<Bootrix.Core.Errors.BootrixException>(() => DmgFixtures.Open(image));

        Assert.Equal(Bootrix.Core.Errors.ErrorCode.ImageTruncated, ex.Code);
    }

    [Fact]
    public void TrailerSectorCountLargerThanTheChunks_PadsWithZeros()
    {
        var volume = ImageTestData.Random(64 * 512, 4);
        var image = new UdifBuilder
        {
            MutateTrailer = t => BinaryPrimitives.WriteUInt64BigEndian(t.AsSpan(0x1EC), 100),
        }.AddPartition(0, "disk image (Apple_HFS : 1)", 0, [ChunkSpec.Zlib(volume)]).Build();

        using var reader = DmgFixtures.Open(image);

        Assert.Equal(100 * 512, reader.Length);
        var all = DmgFixtures.ReadAll(reader);
        Assert.True(all.AsSpan(0, volume.Length).SequenceEqual(volume));
        Assert.All(all.Skip(volume.Length), b => Assert.Equal(0, b));
    }

    [Fact]
    public void PartitionsWithGapsBetweenThem_ReadAsZerosInTheGap()
    {
        var first = ImageTestData.Random(16 * 512, 5);
        var second = ImageTestData.Random(16 * 512, 6);
        var image = new UdifBuilder()
            .AddPartition(0, "a (Apple_HFS : 1)", 0, [ChunkSpec.Raw(first)])
            .AddPartition(1, "b (Apple_HFS : 2)", 40, [ChunkSpec.Zlib(second)])
            .Build();

        using var reader = DmgFixtures.Open(image);

        var all = DmgFixtures.ReadAll(reader);
        Assert.Equal(56 * 512, all.Length);
        Assert.True(all.AsSpan(0, first.Length).SequenceEqual(first));
        Assert.All(all.Skip(first.Length).Take(24 * 512), b => Assert.Equal(0, b));
        Assert.True(all.AsSpan(40 * 512).SequenceEqual(second));
    }

    [Fact]
    public void PartitionsInReverseOrder_AreMappedByTheirSectorNumbers()
    {
        var first = ImageTestData.Random(8 * 512, 7);
        var second = ImageTestData.Text(8 * 512, 8);
        var image = new UdifBuilder()
            .AddPartition(0, "b (Apple_HFS : 2)", 8, [ChunkSpec.Zlib(second)])
            .AddPartition(1, "a (Apple_HFS : 1)", 0, [ChunkSpec.Zlib(first)])
            .Build();

        using var reader = DmgFixtures.Open(image);

        Assert.Equal([.. first, .. second], DmgFixtures.ReadAll(reader));
    }

    [Fact]
    public void OpenFromPath_ReadsTheFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "bootrix-dmg-" + Guid.NewGuid().ToString("N") + ".dmg");
        File.WriteAllBytes(path, DmgFixtures.Build("mixed"));
        try
        {
            using var reader = DmgReader.Open(path);

            Assert.Equal(DmgFixtures.Volume, DmgFixtures.ReadAll(reader));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OpenFromMissingPath_ThrowsControlledError()
    {
        var path = Path.Combine(Path.GetTempPath(), "bootrix-missing-" + Guid.NewGuid().ToString("N") + ".dmg");

        var ex = Assert.Throws<Bootrix.Core.Errors.BootrixException>(() => DmgReader.Open(path));

        Assert.Equal(Bootrix.Core.Errors.ErrorCode.ImageUnreadable, ex.Code);
    }
}
