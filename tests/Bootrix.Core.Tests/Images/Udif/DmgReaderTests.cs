// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images.Udif;

namespace Bootrix.Core.Tests.Images.Udif;

public class DmgReaderTests
{
    [Theory]
    [InlineData("raw")]
    [InlineData("zlib")]
    [InlineData("bzip2")]
    [InlineData("adc")]
    [InlineData("mixed")]
    public void Read_EachCodec_ReturnsOriginalVolume(string codec)
    {
        using var reader = DmgFixtures.Open(DmgFixtures.Build(codec));

        Assert.Equal(DmgFixtures.Volume.Length, reader.Length);
        Assert.Equal(DmgFixtures.Volume, DmgFixtures.ReadAll(reader));
    }

    [Fact]
    public void Info_DescribesPartitionsAndChunkTypes()
    {
        using var reader = DmgFixtures.Open(DmgFixtures.Build("mixed"));

        var info = reader.Info;
        Assert.Equal(DmgFixtures.VolumeSectors, info.SectorCount);
        Assert.Equal(DmgFixtures.VolumeSectors * 512L, info.VolumeSize);
        Assert.Equal(DmgFixtures.Partitions.Count, info.Partitions.Count);
        Assert.Equal(-1, info.Partitions[0].Id);
        Assert.Equal("MBR", info.Partitions[0].Type);
        Assert.Equal("Apple_HFS", info.Partitions[3].Type);
        Assert.Equal(34, info.Partitions[3].StartSector);
        Assert.Equal(1500, info.Partitions[3].SectorCount);
        Assert.Contains(UdifChunkType.Zlib, info.ChunkTypes);
        Assert.Contains(UdifChunkType.Bzip2, info.ChunkTypes);
        Assert.Contains(UdifChunkType.Adc, info.ChunkTypes);
        Assert.Contains(UdifChunkType.ZeroFill, info.ChunkTypes);
        Assert.False(info.TrailerAtFront);
        Assert.False(info.UsesResourceFork);
    }

    [Fact]
    public void Read_RandomRanges_MatchTheVolume()
    {
        using var reader = DmgFixtures.Open(DmgFixtures.Build("mixed", chunkSectors: 37));
        var expected = DmgFixtures.Volume;
        var random = new Random(1234);
        var buffer = new byte[200_000];

        for (var i = 0; i < 300; i++)
        {
            var offset = random.NextInt64(0, expected.Length);
            var length = random.Next(1, buffer.Length);
            reader.Position = offset;

            var read = reader.Read(buffer, 0, length);

            var wanted = (int)Math.Min(length, expected.Length - offset);
            Assert.Equal(wanted, read);
            Assert.True(expected.AsSpan((int)offset, read).SequenceEqual(buffer.AsSpan(0, read)), $"mismatch at {offset}");
        }
    }

    [Fact]
    public void Read_AcrossChunkBoundariesByteByByte_Matches()
    {
        using var reader = DmgFixtures.Open(DmgFixtures.Build("zlib", chunkSectors: 8));
        var expected = DmgFixtures.Volume;

        foreach (var boundary in new[] { 4096, 4096 * 5, 512 * 34, 512 * 1534 })
        {
            reader.Position = boundary - 3;
            var buffer = new byte[6];
            Assert.Equal(6, reader.Read(buffer));
            Assert.True(expected.AsSpan(boundary - 3, 6).SequenceEqual(buffer));
        }
    }

    [Fact]
    public void Read_BackwardsAndForwardsRepeatedly_UsesCacheCorrectly()
    {
        var options = new DmgReaderOptions { CacheBytes = 64 * 1024, ReadAhead = 2 };
        using var reader = DmgFixtures.Open(DmgFixtures.Build("bzip2", chunkSectors: 16), options);
        var expected = DmgFixtures.Volume;
        var buffer = new byte[4096];

        for (var pass = 0; pass < 3; pass++)
        {
            for (var offset = expected.Length - buffer.Length; offset >= 0; offset -= 70_001)
            {
                reader.Position = offset;
                reader.ReadExactly(buffer);
                Assert.True(expected.AsSpan(offset, buffer.Length).SequenceEqual(buffer));
            }
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public void CopyTo_WithAndWithoutReadAhead_ProducesIdenticalOutput(int readAhead)
    {
        var options = new DmgReaderOptions { ReadAhead = readAhead, CacheBytes = 1024 * 1024 };
        using var reader = DmgFixtures.Open(DmgFixtures.Build("mixed", chunkSectors: 32), options);

        Assert.Equal(DmgFixtures.Volume, DmgFixtures.ReadAll(reader));
    }

    [Fact]
    public void CopyTo_LargeImageWithRealisticChunkSize_StreamsTheSameBytes()
    {
        // 40 MiB in 1 MiB chunks, the size hdiutil uses; decoded four chunks ahead on other threads.
        var volume = ImageTestData.Volume(40 * 2048, 99);
        var image = UdifBuilder.FromVolume(
            volume, [("disk image (Apple_HFS : 0)", 0, 40 * 2048)], 2048, (index, data) => DmgFixtures.Encode(index % 3 == 0 ? "bzip2" : "zlib", index, data)).Build();
        using var reader = DmgReader.Open(new MemoryStream(image), options: new DmgReaderOptions { ReadAhead = 4, CacheBytes = 8 * 1024 * 1024 });

        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(reader);

        Assert.Equal(System.Security.Cryptography.SHA256.HashData(volume), hash);
    }

    [Fact]
    public void Read_JumpingBetweenSequentialRuns_DoesNotStallOrCorruptReadAhead()
    {
        var options = new DmgReaderOptions { ReadAhead = 3, CacheBytes = 256 * 1024 };
        using var reader = DmgFixtures.Open(DmgFixtures.Build("zlib", chunkSectors: 16), options);
        var expected = DmgFixtures.Volume;
        var random = new Random(77);
        var buffer = new byte[8192];

        for (var run = 0; run < 60; run++)
        {
            var start = random.Next(0, expected.Length - buffer.Length * 20);
            reader.Position = start;
            for (var i = 0; i < 20; i++)
            {
                reader.ReadExactly(buffer);
                Assert.True(expected.AsSpan(start + (i * buffer.Length), buffer.Length).SequenceEqual(buffer));
            }
        }
    }

    [Fact]
    public void Read_PastEnd_ReturnsZero()
    {
        using var reader = DmgFixtures.Open(DmgFixtures.Build("raw"));

        reader.Seek(0, SeekOrigin.End);
        Assert.Equal(0, reader.Read(new byte[10]));
        reader.Position = reader.Length + 4096;
        Assert.Equal(0, reader.Read(new byte[10]));
    }

    [Fact]
    public void Stream_IsReadOnlyAndSeekable()
    {
        using var reader = DmgFixtures.Open(DmgFixtures.Build("raw"));

        Assert.True(reader.CanRead);
        Assert.True(reader.CanSeek);
        Assert.False(reader.CanWrite);
        Assert.Throws<NotSupportedException>(() => reader.Write(new byte[1]));
        Assert.Throws<NotSupportedException>(() => reader.SetLength(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.Seek(-1, SeekOrigin.Begin));
    }

    [Fact]
    public void Dispose_ClosesTheSourceStream()
    {
        var source = new MemoryStream(DmgFixtures.Build("zlib"));
        var reader = DmgReader.Open(source);
        reader.CopyTo(Stream.Null);

        reader.Dispose();

        Assert.False(source.CanRead);
        Assert.Throws<ObjectDisposedException>(() => reader.ReadByte());
    }

    [Fact]
    public void Dispose_WithLeaveOpen_KeepsTheSourceStream()
    {
        var source = new MemoryStream(DmgFixtures.Build("zlib"));

        DmgReader.Open(source, leaveOpen: true).Dispose();

        Assert.True(source.CanRead);
    }

    [Fact]
    public async Task ReadAsync_ReturnsTheSameBytes()
    {
        await using var reader = DmgFixtures.Open(DmgFixtures.Build("adc"));
        var buffer = new byte[100_000];
        reader.Position = 123_456;

        var read = await reader.ReadAsync(buffer, CancellationToken.None);

        Assert.Equal(buffer.Length, read);
        Assert.True(DmgFixtures.Volume.AsSpan(123_456, buffer.Length).SequenceEqual(buffer));
    }
}
