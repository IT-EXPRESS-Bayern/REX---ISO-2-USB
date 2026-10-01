// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Bootrix.Core.Errors;
using Bootrix.Core.Images.Apple;

namespace Bootrix.Core.Tests.Images.Apple;

public class SparseImageStreamTests
{
    private const int Sectors = 8; // 4 KiB bands keep the test files small
    private const int BandBytes = Sectors * 512;

    private static SparseImageStream Open(byte[] file) => SparseImageStream.Open(new MemoryStream(file));

    private static byte[] ReadAll(Stream stream)
    {
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    [Fact]
    public void Read_ImageWithEmptyBands_ReturnsTheVolume()
    {
        var volume = SparseImageBuilder.SparseVolume(9, BandBytes, BandBytes, 1);
        using var stream = Open(SparseImageBuilder.BuildImage(volume, Sectors));

        Assert.Equal(volume.Length, stream.Length);
        Assert.Equal(5, stream.AllocatedBands);
        Assert.Equal(BandBytes, stream.BandSize);
        Assert.Equal(volume, ReadAll(stream));
    }

    [Fact]
    public void Read_BandsStoredInAnotherOrderThanTheirPosition_AreMappedByTheTable()
    {
        var volume = SparseImageBuilder.SparseVolume(7, BandBytes, BandBytes, 2);
        var file = SparseImageBuilder.BuildImage(volume, Sectors, [6, 0, 4, 2]);
        using var stream = Open(file);

        Assert.Equal(volume, ReadAll(stream));
    }

    [Fact]
    public void Read_LastBandShorterThanTheOthers_EndsAtTheVolumeLength()
    {
        var volume = SparseImageBuilder.SparseVolume(5, BandBytes, 1536, 3);
        using var stream = Open(SparseImageBuilder.BuildImage(volume, Sectors));

        Assert.Equal(4 * BandBytes + 1536, stream.Length);
        Assert.Equal(volume, ReadAll(stream));
    }

    [Fact]
    public void Read_RandomRanges_MatchTheVolume()
    {
        var volume = SparseImageBuilder.SparseVolume(40, BandBytes, BandBytes, 4);
        using var stream = Open(SparseImageBuilder.BuildImage(volume, Sectors));
        var random = new Random(5);
        var buffer = new byte[20_000];

        for (var i = 0; i < 200; i++)
        {
            var offset = random.Next(0, volume.Length);
            var length = random.Next(1, buffer.Length);
            stream.Position = offset;

            var read = stream.Read(buffer, 0, length);

            Assert.Equal(Math.Min(length, volume.Length - offset), read);
            Assert.True(volume.AsSpan(offset, read).SequenceEqual(buffer.AsSpan(0, read)));
        }
    }

    [Fact]
    public void Read_RealSparseImageLayout_BandTableDirectionMatchesHdiutil()
    {
        // Layout of a real hdiutil sparse image: table entry n holds the 1-based logical band stored at
        // 4096 + n * band size. Here the file stores logical bands 1, 4, 2, 3 (in that order).
        var bandBytes = 2048 * 512;
        var file = new byte[4096 + (4 * bandBytes)];
        "sprs"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(8), 2048);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(16), 8192);
        foreach (var (slot, logical) in new[] { (0, 1), (1, 4), (2, 2), (3, 3) })
        {
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(64 + (slot * 4)), (uint)logical);
            file.AsSpan(4096 + (slot * bandBytes), bandBytes).Fill((byte)logical);
        }

        using var stream = Open(file);

        var sample = new byte[1];
        for (var logical = 1; logical <= 4; logical++)
        {
            stream.Position = (long)(logical - 1) * bandBytes + 77;
            stream.ReadExactly(sample);
            Assert.Equal(logical, sample[0]);
        }
    }

    [Fact]
    public void Open_FileShorterThanItsBandTable_IsTruncated()
    {
        var volume = SparseImageBuilder.SparseVolume(9, BandBytes, BandBytes, 1);
        var file = SparseImageBuilder.BuildImage(volume, Sectors);

        var ex = Assert.Throws<BootrixException>(() => Open(file[..^BandBytes]));

        Assert.Equal(ErrorCode.ImageTruncated, ex.Code);
    }

    [Fact]
    public void Open_MoreBandsThanTheHeaderHolds_IsUnsupportedRatherThanGuessed()
    {
        var file = new byte[4096];
        "sprs"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(8), 2048);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(16), 2048 * 2000);

        var ex = Assert.Throws<BootrixException>(() => Open(file));

        Assert.Equal(ErrorCode.ImageUnsupported, ex.Code);
    }

    [Theory]
    [InlineData(0u, 100u)]
    [InlineData(8u, 0u)]
    [InlineData(uint.MaxValue, 100u)]
    public void Open_InvalidGeometry_IsCorruption(uint sectorsPerBand, uint totalSectors)
    {
        var file = new byte[4096];
        "sprs"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(8), sectorsPerBand);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(16), totalSectors);

        Assert.Equal(ErrorCode.ImageCorrupt, Assert.Throws<BootrixException>(() => Open(file)).Code);
    }

    [Fact]
    public void Open_BandTableWithDuplicateOrOutOfRangeEntries_IsCorruption()
    {
        var volume = SparseImageBuilder.SparseVolume(5, BandBytes, BandBytes, 1);
        var duplicate = SparseImageBuilder.BuildImage(volume, Sectors);
        BinaryPrimitives.WriteUInt32BigEndian(duplicate.AsSpan(64 + 4), 1);
        var outOfRange = SparseImageBuilder.BuildImage(volume, Sectors);
        BinaryPrimitives.WriteUInt32BigEndian(outOfRange.AsSpan(64), 99);

        Assert.Equal(ErrorCode.ImageCorrupt, Assert.Throws<BootrixException>(() => Open(duplicate)).Code);
        Assert.Equal(ErrorCode.ImageCorrupt, Assert.Throws<BootrixException>(() => Open(outOfRange)).Code);
    }

    [Fact]
    public void Open_FileWithoutHeader_IsRejected()
    {
        Assert.Equal(ErrorCode.ImageCorrupt, Assert.Throws<BootrixException>(() => Open(ImageTestData.Random(5000, 1))).Code);
        Assert.Equal(ErrorCode.ImageCorrupt, Assert.Throws<BootrixException>(() => Open([])).Code);
    }

    [Fact]
    public void Open_EncryptedSparseImage_IsRejectedAsEncrypted()
    {
        var file = new byte[8192];
        "encrcdsa"u8.CopyTo(file);

        Assert.Equal(ErrorCode.ImageEncrypted, Assert.Throws<BootrixException>(() => Open(file)).Code);
    }

    [Fact]
    public void Open_FromPath_ReadsTheFileAndReleasesIt()
    {
        var volume = SparseImageBuilder.SparseVolume(9, BandBytes, BandBytes, 1);
        var path = Path.Combine(Path.GetTempPath(), "bootrix-sprs-" + Guid.NewGuid().ToString("N") + ".sparseimage");
        File.WriteAllBytes(path, SparseImageBuilder.BuildImage(volume, Sectors));
        try
        {
            using (var stream = SparseImageStream.Open(path))
            {
                Assert.Equal(volume, ReadAll(stream));
            }

            File.Delete(path);
            Assert.False(File.Exists(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Mutations_OfTheHeader_OnlyEverFailInControlledWays()
    {
        var volume = SparseImageBuilder.SparseVolume(9, BandBytes, BandBytes, 1);
        var original = SparseImageBuilder.BuildImage(volume, Sectors);
        var random = new Random(8);

        for (var i = 0; i < 500; i++)
        {
            var file = (byte[])original.Clone();
            for (var flips = random.Next(1, 4); flips > 0; flips--)
            {
                file[random.Next(0, 128)] = (byte)random.Next(256);
            }

            try
            {
                using var stream = Open(file);
                stream.CopyTo(Stream.Null);
            }
            catch (BootrixException)
            {
            }
        }
    }
}
