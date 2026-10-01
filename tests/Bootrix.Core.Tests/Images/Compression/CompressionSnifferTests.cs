// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bootrix.Core.Images.Compression;
using Bootrix.Core.Tests.Images.Support;

namespace Bootrix.Core.Tests.Images.Compression;

public sealed class CompressionSnifferTests : IDisposable
{
    private readonly TestDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private byte[] HeadOf(string tool, string[] arguments, byte[]? data = null)
    {
        var raw = _dir.Write("raw.bin", data ?? TestDirectory.Compressible(300_000));
        var target = _dir.File("out." + tool);
        ReferenceTool.Run(tool, arguments, _dir.Path, raw, target);
        return File.ReadAllBytes(target).AsSpan(0, CompressionSniffer.HeaderLength).ToArray();
    }

    [ToolFact("xz")]
    public void Xz_IsRecognised() => Assert.Equal(CompressionFormat.Xz, CompressionSniffer.Detect(HeadOf("xz", ["-c"])));

    [ToolFact("gzip")]
    public void GZip_IsRecognised() => Assert.Equal(CompressionFormat.GZip, CompressionSniffer.Detect(HeadOf("gzip", ["-c"])));

    [ToolFact("bzip2")]
    public void BZip2_IsRecognisedAtEveryBlockSize()
    {
        for (var level = 1; level <= 9; level++)
        {
            Assert.Equal(CompressionFormat.BZip2, CompressionSniffer.Detect(HeadOf("bzip2", ["-c", "-" + level.ToString(CultureInfo.InvariantCulture)])));
        }
    }

    [ToolFact("zstd")]
    public void Zstd_IsRecognised() => Assert.Equal(CompressionFormat.Zstd, CompressionSniffer.Detect(HeadOf("zstd", ["-c", "-q"])));

    [ToolFact("compress")]
    public void Compress_IsRecognised() => Assert.Equal(CompressionFormat.Compress, CompressionSniffer.Detect(HeadOf("compress", ["-c"])));

    [ToolFact("zip")]
    public void Zip_IsRecognised()
    {
        var raw = _dir.Write("payload.img", TestDirectory.Compressible(100_000));
        var zip = _dir.File("a.zip");
        ReferenceTool.Run("zip", ["-q", "-j", zip, raw], null, null, null);

        Assert.Equal(CompressionFormat.Zip, CompressionSniffer.Detect(File.ReadAllBytes(zip).AsSpan(0, CompressionSniffer.HeaderLength)));
    }

    [ToolTheory("xz")]
    [InlineData("-0")]
    [InlineData("-1")]
    [InlineData("-3")]
    [InlineData("-6")]
    [InlineData("-9")]
    [InlineData("-9e")]
    [InlineData("--lzma1=preset=6,dict=1MiB,lc=0,lp=2,pb=0")]
    [InlineData("--lzma1=preset=6,dict=3MiB,lc=4,lp=0,pb=4")]
    public void LzmaAlone_IsRecognisedByItsHeaderForEveryPresetAndProperty(string setting)
    {
        Assert.Equal(CompressionFormat.Lzma, CompressionSniffer.Detect(HeadOf("xz", ["-c", "--format=lzma", setting])));
    }

    [ToolFact("xz")]
    public void LzmaAlone_OfEmptyInput_IsRecognised() =>
        Assert.Equal(CompressionFormat.Lzma, CompressionSniffer.Detect(HeadOf("xz", ["-c", "--format=lzma"], [])));

    [Fact]
    public void ZstdSkippableFrame_CountsAsZstd()
    {
        var head = new byte[CompressionSniffer.HeaderLength];
        head[0] = 0x5A;
        head[1] = 0x2A;
        head[2] = 0x4D;
        head[3] = 0x18;

        Assert.Equal(CompressionFormat.Zstd, CompressionSniffer.Detect(head));
    }

    [Theory]
    [InlineData(new byte[] { }, CompressionFormat.None)]
    [InlineData(new byte[] { 0x1F }, CompressionFormat.None)]
    [InlineData(new byte[] { 0x1F, 0x8B }, CompressionFormat.None)]
    [InlineData(new byte[] { 0x1F, 0x8B, 0x08 }, CompressionFormat.GZip)]
    [InlineData(new byte[] { 0x1F, 0x8B, 0x07 }, CompressionFormat.None)]
    [InlineData(new byte[] { 0x1F, 0x9D, 0x90 }, CompressionFormat.Compress)]
    [InlineData(new byte[] { 0x1F, 0x9D, 0x8C }, CompressionFormat.Compress)]
    [InlineData(new byte[] { 0x1F, 0x9D, 0x88 }, CompressionFormat.None)]
    [InlineData(new byte[] { 0x1F, 0x9D, 0xB0 }, CompressionFormat.None)]
    [InlineData(new byte[] { 0x28, 0xB5, 0x2F, 0xFD }, CompressionFormat.Zstd)]
    [InlineData(new byte[] { 0x28, 0xB5, 0x2F }, CompressionFormat.None)]
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04 }, CompressionFormat.Zip)]
    [InlineData(new byte[] { 0x50, 0x4B, 0x05, 0x06 }, CompressionFormat.None)]
    public void ShortHeads_AreClassifiedByTheirMagic(byte[] head, CompressionFormat expected) =>
        Assert.Equal(expected, CompressionSniffer.Detect(head));

    [Fact]
    public void BZip2_NeedsTheBlockMagicAfterTheLevelDigit()
    {
        var valid = "BZh9\x31\x41\x59\x26\x53\x59"u8.ToArray();
        var wrongLevel = "BZh0\x31\x41\x59\x26\x53\x59"u8.ToArray();
        var noBlock = "BZh9\x00\x00\x00\x00\x00\x00"u8.ToArray();

        Assert.Equal(CompressionFormat.BZip2, CompressionSniffer.Detect(valid));
        Assert.Equal(CompressionFormat.None, CompressionSniffer.Detect(wrongLevel));
        Assert.Equal(CompressionFormat.None, CompressionSniffer.Detect(noBlock));
        Assert.Equal(CompressionFormat.None, CompressionSniffer.Detect("BZh9"u8));
    }

    [Fact]
    public void XzMagic_NeedsAllSixBytes()
    {
        Assert.Equal(CompressionFormat.Xz, CompressionSniffer.Detect([0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00]));
        Assert.Equal(CompressionFormat.None, CompressionSniffer.Detect([0xFD, 0x37, 0x7A, 0x58, 0x5A]));
        Assert.Equal(CompressionFormat.None, CompressionSniffer.Detect([0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x01]));
    }

    [Fact]
    public void RealImagesAndPartitionTables_AreNotCompressed()
    {
        var iso = new byte[CompressionSniffer.HeaderLength];
        var mbr = new byte[CompressionSniffer.HeaderLength];
        mbr[0] = 0xEB;
        mbr[1] = 0x63;
        mbr[2] = 0x90;
        var wim = new byte[CompressionSniffer.HeaderLength];
        "MSWIM\0\0\0"u8.CopyTo(wim);

        Assert.Equal(CompressionFormat.None, CompressionSniffer.Detect(iso));
        Assert.Equal(CompressionFormat.None, CompressionSniffer.Detect(mbr));
        Assert.Equal(CompressionFormat.None, CompressionSniffer.Detect(wim));
    }

    /// <summary>LZMA-alone has no signature at all, so random data must not be mistaken for it.</summary>
    [Fact]
    public void RandomData_IsNeverTakenForACompressedFile()
    {
        var random = new Random(20260101);
        var head = new byte[CompressionSniffer.HeaderLength];
        var falsePositives = 0;

        for (var round = 0; round < 200_000; round++)
        {
            random.NextBytes(head);
            if (CompressionSniffer.Detect(head) != CompressionFormat.None)
            {
                falsePositives++;
            }
        }

        // Expected rate is about 2^-23 for gzip and far lower for the others.
        Assert.True(falsePositives <= 1, $"{falsePositives} false positives");
    }

    [Fact]
    public void LzmaAloneHeader_WithImplausibleFields_IsRejected()
    {
        byte[] Header(byte properties, uint dictionary, ulong size, byte first)
        {
            var head = new byte[CompressionSniffer.HeaderLength];
            head[0] = properties;
            BitConverter.TryWriteBytes(head.AsSpan(1), dictionary);
            BitConverter.TryWriteBytes(head.AsSpan(5), size);
            head[13] = first;
            return head;
        }

        Assert.Equal(CompressionFormat.Lzma, CompressionSniffer.Detect(Header(0x5D, 1 << 24, ulong.MaxValue, 0)));
        Assert.Equal(CompressionFormat.Lzma, CompressionSniffer.Detect(Header(0x5D, 3 << 22, 1_000_000, 0)));
        Assert.Equal(CompressionFormat.None, CompressionSniffer.Detect(Header(225, 1 << 24, ulong.MaxValue, 0)));
        Assert.Equal(CompressionFormat.None, CompressionSniffer.Detect(Header(0x5D, 1000, ulong.MaxValue, 0)));
        Assert.Equal(CompressionFormat.None, CompressionSniffer.Detect(Header(0x5D, (1 << 24) + 1, ulong.MaxValue, 0)));
        Assert.Equal(CompressionFormat.None, CompressionSniffer.Detect(Header(0x5D, 1 << 24, 1UL << 60, 0)));
        Assert.Equal(CompressionFormat.None, CompressionSniffer.Detect(Header(0x5D, 1 << 24, ulong.MaxValue, 1)));
    }
}
