// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using Bootrix.Core.Errors;
using Bootrix.Core.Storage;
using Bootrix.Core.Writing.Raw;

namespace Bootrix.Core.Tests.Writing.Raw;

public sealed class BlockMapTests
{
    private static BlockMap Fixture() => BlockMapParser.Parse(BmaptoolFixture.BlockMapBytes);

    [Fact]
    public void Parse_MapWrittenByBmaptool_ReadsHeaderAndRanges()
    {
        var map = Fixture();

        Assert.Equal("2.0", map.Version);
        Assert.Equal(BmaptoolFixture.ImageSize, map.ImageSize);
        Assert.Equal(4096, map.BlockSize);
        Assert.Equal(2048, map.BlocksCount);
        Assert.Equal(9, map.MappedBlocksCount);
        Assert.Equal("sha256", map.ChecksumType);
        Assert.Equal([(0L, 0L), (256L, 260L), (1464L, 1466L)], map.Ranges.Select(r => (r.FirstBlock, r.LastBlock)));
        Assert.All(map.Ranges, range => Assert.Equal(64, range.Checksum!.Length));
    }

    [Fact]
    public void Parse_MapWrittenByBmaptool_HasAValidFileChecksum()
    {
        Assert.True(Fixture().FileChecksumValid);
    }

    [Fact]
    public void Parse_AlteredMap_ReportsTheFileChecksumAsWrong()
    {
        var text = Encoding.UTF8.GetString(BmaptoolFixture.BlockMapBytes).Replace("256-260", "256-259", StringComparison.Ordinal);

        var map = BlockMapParser.Parse(Encoding.UTF8.GetBytes(text.Replace("<MappedBlocksCount> 9 ", "<MappedBlocksCount> 8 ", StringComparison.Ordinal)));

        Assert.False(map.FileChecksumValid);
    }

    [Fact]
    public void ToByteRanges_ConvertsBlocksToBytes()
    {
        var ranges = Fixture().ToByteRanges();

        Assert.Equal([new ByteRange(0, 4096), new ByteRange(256 * 4096, 5 * 4096), new ByteRange(1464 * 4096, 3 * 4096)], ranges);
    }

    [Fact]
    public void ToByteRanges_CutsTheLastBlockAtTheImageSize()
    {
        var map = BlockMapParser.Parse(Build("2.0", imageSize: 10_000, blocks: 3, mapped: 1, "<Range> 2 </Range>"));

        Assert.Equal([new ByteRange(8192, 1808)], map.ToByteRanges());
    }

    [Fact]
    public void Describes_ComparesTheImageSize()
    {
        var map = Fixture();

        Assert.True(map.Describes(BmaptoolFixture.ImageSize));
        Assert.False(map.Describes(BmaptoolFixture.ImageSize + 1));
    }

    [Fact]
    public void Parse_Format1_UsesSha1AndTheShaAttribute()
    {
        var chunk = new byte[4096];
        new Random(1).NextBytes(chunk);
        var sha1 = Convert.ToHexStringLower(CryptographicOperations.HashData(HashAlgorithmName.SHA1, chunk));

        var map = BlockMapParser.Parse(Build("1.2", imageSize: 16_384, blocks: 4, mapped: 1, $"<Range sha1=\"{sha1}\"> 1 </Range>", withChecksumType: false));

        Assert.Equal("sha1", map.ChecksumType);
        Assert.Equal(sha1, map.Ranges[0].Checksum);

        var image = new byte[16_384];
        chunk.CopyTo(image, 4096);
        using var verifier = BlockMapVerifier.Create(map)!;
        verifier.Observe(0, image);
        Assert.Equal(1, verifier.RangesVerified);
    }

    [Theory]
    [InlineData("<bmap><BlockSize>4096</BlockSize></bmap>", "version")]
    [InlineData("<bmap version=\"2.0\"><BlockSize>0</BlockSize></bmap>", "block size")]
    [InlineData("<bmap version=\"2.0\"><BlockSize>abc</BlockSize></bmap>", "number")]
    [InlineData("not xml at all", "")]
    [InlineData("<!DOCTYPE bmap [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><bmap version=\"2.0\"/>", "")]
    public void Parse_BrokenFiles_ThrowBlockMapInvalid(string xml, string detail)
    {
        var error = Assert.Throws<BootrixException>(() => BlockMapParser.Parse(Encoding.UTF8.GetBytes(xml)));

        Assert.Equal(ErrorCode.BlockMapInvalid, error.Code);
        Assert.Contains(detail, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_RangeBeyondTheImage_IsRejected()
    {
        var content = Build("2.0", imageSize: 16_384, blocks: 4, mapped: 1, "<Range> 7 </Range>");

        Assert.Equal(ErrorCode.BlockMapInvalid, Assert.Throws<BootrixException>(() => BlockMapParser.Parse(content)).Code);
    }

    [Fact]
    public void Parse_MappedCountThatDisagreesWithTheRanges_IsRejected()
    {
        var content = Build("2.0", imageSize: 16_384, blocks: 4, mapped: 3, "<Range> 1 </Range>");

        Assert.Equal(ErrorCode.BlockMapInvalid, Assert.Throws<BootrixException>(() => BlockMapParser.Parse(content)).Code);
    }

    [Fact]
    public void Verifier_AcceptsTheImage_WhateverTheChunking()
    {
        var image = BmaptoolFixture.Image();
        foreach (var chunk in new[] { 1000, 4096, 65_536, 1 << 20, image.Length })
        {
            using var verifier = BlockMapVerifier.Create(Fixture())!;
            for (var offset = 0; offset < image.Length; offset += chunk)
            {
                verifier.Observe(offset, image.AsSpan(offset, Math.Min(chunk, image.Length - offset)));
            }

            verifier.Complete(image.Length);
            Assert.Equal(3, verifier.RangesVerified);
        }
    }

    [Theory]
    [InlineData(100)]
    [InlineData(256 * 4096 + 3 * 4096 + 17)]
    [InlineData(1466 * 4096 + 4095)]
    public void Verifier_RejectsAChangedByteInAMappedRange(int position)
    {
        var image = BmaptoolFixture.Image();
        image[position] ^= 1;
        using var verifier = BlockMapVerifier.Create(Fixture())!;

        var error = Assert.Throws<BootrixException>(() =>
        {
            for (var offset = 0; offset < image.Length; offset += 65_536)
            {
                verifier.Observe(offset, image.AsSpan(offset, 65_536));
            }
        });

        Assert.Equal(ErrorCode.ImageHashMismatch, error.Code);
    }

    [Fact]
    public void Verifier_IgnoresChangesInTheGaps()
    {
        var image = BmaptoolFixture.Image();
        image[4096 * 100] = 0xFF;
        using var verifier = BlockMapVerifier.Create(Fixture())!;

        verifier.Observe(0, image);

        Assert.Equal(3, verifier.RangesVerified);
    }

    [Fact]
    public void Verifier_ComplainsWhenTheImageIsShorterThanTheMap()
    {
        var image = BmaptoolFixture.Image();
        using var verifier = BlockMapVerifier.Create(Fixture())!;
        verifier.Observe(0, image.AsSpan(0, 300 * 4096));

        Assert.Equal(ErrorCode.ImageHashMismatch, Assert.Throws<BootrixException>(() => verifier.Complete(300 * 4096)).Code);
    }

    [Fact]
    public void Verifier_IsNotCreatedForMapsWithoutChecksums()
    {
        var map = BlockMapParser.Parse(Build("2.0", imageSize: 16_384, blocks: 4, mapped: 1, "<Range> 1 </Range>"));

        Assert.Null(BlockMapVerifier.Create(map));
    }

    [Fact]
    public async Task Writer_WithTheBmaptoolMap_WritesNineBlocksAndLeavesTheRest()
    {
        var image = BmaptoolFixture.Image();
        var path = Path.Combine(Path.GetTempPath(), "bootrix-bmap-" + Guid.NewGuid().ToString("N") + ".img");
        try
        {
            using var device = new FileBlockDevice(path, 16 << 20);
            device.Write(2 << 20, Enumerable.Repeat((byte)0x77, 4096).ToArray());
            var map = Fixture();
            using var verifier = BlockMapVerifier.Create(map)!;

            var report = await new RawImageWriter().WriteAsync(
                new MemoryStream(image),
                image.Length,
                [device],
                new RawWriteOptions { Sparse = map.ToWriteMap(fillGaps: false), SourceObserver = verifier.Observe });
            verifier.Complete(report.ImageBytes);

            Assert.True(report.AllSucceeded && report.Targets[0].Verified);
            Assert.Equal(9 * 4096, report.Targets[0].BytesWritten);
            Assert.Equal(image.Length - (9 * 4096), report.SkippedBytes);
            var back = new byte[image.Length];
            device.Read(0, back);
            foreach (var range in map.ToByteRanges())
            {
                Assert.Equal(image[(int)range.Start..(int)range.End], back[(int)range.Start..(int)range.End]);
            }

            // A hole of the image is not touched: what the target held there stays.
            var untouched = new byte[4096];
            device.Read(2 << 20, untouched);
            Assert.All(untouched, b => Assert.Equal(0x77, b));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Writer_WithAMapOfAnotherImage_IsStoppedByTheChecksums()
    {
        var image = BmaptoolFixture.Image();
        image[300 * 4096 - 1] = 1;
        image[257 * 4096] ^= 0x10;
        var path = Path.Combine(Path.GetTempPath(), "bootrix-bmap-" + Guid.NewGuid().ToString("N") + ".img");
        try
        {
            using var device = new FileBlockDevice(path, 16 << 20);
            var map = Fixture();
            using var verifier = BlockMapVerifier.Create(map)!;

            var error = await Assert.ThrowsAsync<BootrixException>(() => new RawImageWriter().WriteAsync(
                new MemoryStream(image),
                image.Length,
                [device],
                new RawWriteOptions { Sparse = map.ToWriteMap(fillGaps: false), SourceObserver = verifier.Observe }));

            Assert.Equal(ErrorCode.ImageHashMismatch, error.Code);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>A block map in the layout bmaptool writes, without the checksum of the file itself.</summary>
    private static byte[] Build(string version, long imageSize, long blocks, long mapped, string ranges, bool withChecksumType = true) =>
        Encoding.UTF8.GetBytes($"""
            <?xml version="1.0" ?>
            <bmap version="{version}">
                <ImageSize> {imageSize} </ImageSize>
                <BlockSize> 4096 </BlockSize>
                <BlocksCount> {blocks} </BlocksCount>
                <MappedBlocksCount> {mapped} </MappedBlocksCount>
                {(withChecksumType ? "<ChecksumType> sha256 </ChecksumType>" : string.Empty)}
                <BlockMap>
                    {ranges}
                </BlockMap>
            </bmap>
            """);
}
