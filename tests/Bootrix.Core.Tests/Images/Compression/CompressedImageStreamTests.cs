// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using Bootrix.Core.Errors;
using Bootrix.Core.Images.Compression;
using Bootrix.Core.Tests.Images.Support;

namespace Bootrix.Core.Tests.Images.Compression;

public sealed class CompressedImageStreamTests : IDisposable
{
    private static readonly byte[] Payload = TestDirectory.Compressible(6 * 1024 * 1024);

    private readonly TestDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private string RawFile(byte[]? data = null) => _dir.Write("raw.img", data ?? Payload);

    private string Compress(string tool, string[] arguments, string output, byte[]? data = null)
    {
        var raw = RawFile(data);
        var target = _dir.File(output);
        ExternalTool.Run(tool, arguments, _dir.Path, raw, target);
        return target;
    }

    private static byte[] ReadAll(CompressedImageStream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static void AssertSame(byte[] expected, byte[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        Assert.Equal(SHA256.HashData(expected), SHA256.HashData(actual));
    }

    [ToolFact("gzip")]
    public void GZip_RoundTrip_ReportsOnlyTheSizeHint()
    {
        var path = Compress("gzip", ["-c", "-6"], "a.gz");

        using var stream = CompressedImageStream.Open(path);

        Assert.Equal(CompressionFormat.GZip, stream.Format);
        Assert.Null(stream.UncompressedLength);
        Assert.Equal(Payload.Length, stream.UncompressedLengthHint);
        AssertSame(Payload, ReadAll(stream));
        Assert.Equal(new FileInfo(path).Length, stream.CompressedBytesConsumed);
    }

    [ToolFact("gzip")]
    public void GZip_ConcatenatedMembers_AreJoined()
    {
        var first = Payload.AsSpan(0, 1_000_000).ToArray();
        var second = Payload.AsSpan(1_000_000, 700_000).ToArray();
        var a = Compress("gzip", ["-c"], "a.gz", first);
        var b = Compress("gzip", ["-c"], "b.gz", second);
        var joined = _dir.Write("joined.gz", [.. File.ReadAllBytes(a), .. File.ReadAllBytes(b)]);

        using var stream = CompressedImageStream.Open(joined);

        AssertSame([.. first, .. second], ReadAll(stream));
    }

    [ToolFact("gzip")]
    public void GZip_HeaderWithAllOptionalFields_IsParsed()
    {
        var data = Payload.AsSpan(0, 300_000).ToArray();
        var header = new List<byte> { 0x1F, 0x8B, 8, 0x1E, 1, 2, 3, 4, 0, 3 };
        header.AddRange([6, 0, (byte)'a', (byte)'b', 2, 0, 9, 9]);
        header.AddRange("disk.img\0"u8.ToArray());
        header.AddRange("a comment\0"u8.ToArray());
        var headerCrc = (ushort)System.IO.Hashing.Crc32.HashToUInt32(header.ToArray());
        header.AddRange(BitConverter.GetBytes(headerCrc));

        using var file = new MemoryStream();
        file.Write(header.ToArray());
        using (var deflate = new System.IO.Compression.DeflateStream(file, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            deflate.Write(data);
        }

        file.Write(BitConverter.GetBytes(System.IO.Hashing.Crc32.HashToUInt32(data)));
        file.Write(BitConverter.GetBytes((uint)data.Length));
        var path = _dir.Write("fields.gz", file.ToArray());
        ExternalTool.Run("gzip", ["-t", path], null, null, null);

        using var stream = CompressedImageStream.Open(path);

        AssertSame(data, ReadAll(stream));
    }

    [ToolFact("gzip")]
    public void GZip_ZeroPaddingAfterTheLastMember_IsAccepted()
    {
        var path = Compress("gzip", ["-c"], "padded.gz", Payload.AsSpan(0, 100_000).ToArray());
        File.AppendAllText(path, new string('\0', 512));

        using var stream = CompressedImageStream.Open(path);

        AssertSame(Payload.AsSpan(0, 100_000).ToArray(), ReadAll(stream));
    }

    [ToolFact("gzip")]
    public void GZip_GarbageAfterTheLastMember_IsRejected()
    {
        var path = Compress("gzip", ["-c"], "garbage.gz", Payload.AsSpan(0, 100_000).ToArray());
        File.AppendAllText(path, "not a gzip member");

        using var stream = CompressedImageStream.Open(path);

        Assert.Throws<BootrixException>(() => ReadAll(stream));
    }

    [ToolFact("gzip")]
    public void GZip_CorruptedPayload_IsRejected()
    {
        var path = Compress("gzip", ["-c"], "flip.gz");
        var bytes = File.ReadAllBytes(path);
        bytes[bytes.Length / 2] ^= 0x55;
        File.WriteAllBytes(path, bytes);

        using var stream = CompressedImageStream.Open(path);

        Assert.Throws<BootrixException>(() => ReadAll(stream));
    }

    [ToolFact("bzip2")]
    public void BZip2_RoundTrip()
    {
        var path = Compress("bzip2", ["-c", "-9"], "a.bz2");

        using var stream = CompressedImageStream.Open(path);

        Assert.Equal(CompressionFormat.BZip2, stream.Format);
        Assert.Null(stream.UncompressedLength);
        AssertSame(Payload, ReadAll(stream));
    }

    [ToolFact("bzip2")]
    public void BZip2_ConcatenatedStreams_AreJoined()
    {
        var first = Payload.AsSpan(0, 900_000).ToArray();
        var second = Payload.AsSpan(900_000, 500_000).ToArray();
        var a = Compress("bzip2", ["-c"], "a.bz2", first);
        var b = Compress("bzip2", ["-c"], "b.bz2", second);
        var joined = _dir.Write("joined.bz2", [.. File.ReadAllBytes(a), .. File.ReadAllBytes(b)]);

        using var stream = CompressedImageStream.Open(joined);

        AssertSame([.. first, .. second], ReadAll(stream));
    }

    [ToolTheory("xz")]
    [InlineData("--check=crc32")]
    [InlineData("--check=crc64")]
    [InlineData("--check=sha256")]
    [InlineData("--check=none")]
    public void Xz_RoundTrip_ReportsExactSizeFromIndex(string check)
    {
        var path = Compress("xz", ["-c", "-3", check], "a.xz");

        using var stream = CompressedImageStream.Open(path);

        Assert.Equal(CompressionFormat.Xz, stream.Format);
        Assert.Equal(Payload.Length, stream.UncompressedLength);
        AssertSame(Payload, ReadAll(stream));
    }

    [ToolFact("xz")]
    public void Xz_MultiBlockStream_IsDecodedAndSizedFromTheIndex()
    {
        var path = Compress("xz", ["-c", "-1", "-T2", "--block-size=1MiB"], "blocks.xz");

        using var stream = CompressedImageStream.Open(path);

        Assert.Equal(Payload.Length, stream.UncompressedLength);
        AssertSame(Payload, ReadAll(stream));
    }

    [ToolFact("xz")]
    public void Xz_ConcatenatedStreamsWithPadding_SumTheSizes()
    {
        var first = Payload.AsSpan(0, 1_200_000).ToArray();
        var second = Payload.AsSpan(1_200_000, 800_000).ToArray();
        var a = Compress("xz", ["-c", "-1"], "a.xz", first);
        var b = Compress("xz", ["-c", "-1", "--check=sha256"], "b.xz", second);
        var joined = _dir.Write("joined.xz", [.. File.ReadAllBytes(a), 0, 0, 0, 0, 0, 0, 0, 0, .. File.ReadAllBytes(b), 0, 0, 0, 0]);

        using var stream = CompressedImageStream.Open(joined);

        Assert.Equal(2_000_000, stream.UncompressedLength);
        AssertSame([.. first, .. second], ReadAll(stream));
    }

    [ToolFact("zstd")]
    public void Zstd_FileInput_ReportsContentSizeFromFrameHeader()
    {
        var raw = RawFile();
        var target = _dir.File("a.zst");
        ExternalTool.Run("zstd", ["-q", "-3", "-o", target, raw], null, null, null);

        using var stream = CompressedImageStream.Open(target);

        Assert.Equal(CompressionFormat.Zstd, stream.Format);
        Assert.Equal(Payload.Length, stream.UncompressedLength);
        AssertSame(Payload, ReadAll(stream));
    }

    [ToolFact("zstd")]
    public void Zstd_PipedInput_HasNoContentSize()
    {
        var path = Compress("zstd", ["-c", "-q", "-3", "--no-check"], "pipe.zst");

        using var stream = CompressedImageStream.Open(path);

        Assert.Null(stream.UncompressedLength);
        AssertSame(Payload, ReadAll(stream));
    }

    [ToolFact("zstd")]
    public void Zstd_MultipleFrames_SumTheSizesAndAreJoined()
    {
        var first = Payload.AsSpan(0, 1_500_000).ToArray();
        var second = Payload.AsSpan(1_500_000, 600_000).ToArray();
        var rawFirst = _dir.Write("first.bin", first);
        var rawSecond = _dir.Write("second.bin", second);
        ExternalTool.Run("zstd", ["-q", "-o", _dir.File("first.zst"), rawFirst], null, null, null);
        ExternalTool.Run("zstd", ["-q", "--long=27", "-o", _dir.File("second.zst"), rawSecond], null, null, null);
        var joined = _dir.Write("joined.zst", [.. File.ReadAllBytes(_dir.File("first.zst")), .. File.ReadAllBytes(_dir.File("second.zst"))]);

        using var stream = CompressedImageStream.Open(joined);

        Assert.Equal(2_100_000, stream.UncompressedLength);
        AssertSame([.. first, .. second], ReadAll(stream));
    }

    [ToolFact("xz")]
    public void Lzma_Alone_RoundTrip()
    {
        var path = Compress("xz", ["-c", "--format=lzma", "-3"], "a.lzma");

        using var stream = CompressedImageStream.Open(path);

        Assert.Equal(CompressionFormat.Lzma, stream.Format);
        AssertSame(Payload, ReadAll(stream));
    }

    [ToolFact("xz")]
    public void Lzma_AloneWithKnownSize_ReportsTheSize()
    {
        // xz only writes streams of unknown length; the size field is filled in the way other encoders do.
        var path = Compress("xz", ["-c", "--format=lzma", "-3"], "known.lzma");
        var bytes = File.ReadAllBytes(path);
        BitConverter.TryWriteBytes(bytes.AsSpan(5), (long)Payload.Length);
        File.WriteAllBytes(path, bytes);

        using var stream = CompressedImageStream.Open(path);

        Assert.Equal(CompressionFormat.Lzma, stream.Format);
        Assert.Equal(Payload.Length, stream.UncompressedLength);
        AssertSame(Payload, ReadAll(stream));
    }

    // -b 9 is left out on purpose: ncompress 5.0 writes a stream at that width that neither it nor gzip can decode.
    [ToolTheory("compress")]
    [InlineData(16)]
    [InlineData(14)]
    [InlineData(12)]
    [InlineData(10)]
    public void Compress_RoundTrip_CoversTableResetsAtEveryCodeWidth(int maxBits)
    {
        var path = Compress("compress", ["-c", "-b", maxBits.ToString(System.Globalization.CultureInfo.InvariantCulture)], "a.Z");

        using var stream = CompressedImageStream.Open(path);

        Assert.Equal(CompressionFormat.Compress, stream.Format);
        Assert.Null(stream.UncompressedLength);
        AssertSame(Payload, ReadAll(stream));
    }

    [ToolFact("compress")]
    public void Compress_ReadInSingleBytes_ProducesTheSameData()
    {
        var data = Payload.AsSpan(0, 120_000).ToArray();
        var path = Compress("compress", ["-c"], "small.Z", data);

        using var stream = CompressedImageStream.Open(path);
        var result = new byte[data.Length];
        var one = new byte[1];
        for (var i = 0; i < result.Length; i++)
        {
            Assert.Equal(1, stream.Read(one, 0, 1));
            result[i] = one[0];
        }

        Assert.Equal(data, result);
        Assert.Equal(0, stream.Read(one, 0, 1));
    }

    [ToolFact("compress", "uncompress")]
    public void Compress_OutputMatchesTheReferenceDecompressor()
    {
        var path = Compress("compress", ["-c"], "ref.Z");
        var reference = _dir.File("ref.out");
        ExternalTool.Run("uncompress", ["-c", path], null, null, reference);

        using var stream = CompressedImageStream.Open(path);

        AssertSame(File.ReadAllBytes(reference), ReadAll(stream));
    }

    [ToolFact("zip")]
    public void Zip_SingleEntry_ReportsSizeAndName()
    {
        var raw = RawFile();
        var zip = _dir.File("one.zip");
        ExternalTool.Run("zip", ["-q", "-j", zip, raw], null, null, null);

        using var stream = CompressedImageStream.Open(zip);

        Assert.Equal(CompressionFormat.Zip, stream.Format);
        Assert.Equal("raw.img", stream.EntryName);
        Assert.Equal(Payload.Length, stream.UncompressedLength);
        AssertSame(Payload, ReadAll(stream));
        Assert.InRange(stream.CompressedBytesConsumed, 1, new FileInfo(zip).Length);
    }

    [ToolFact("zip")]
    public void Zip_ForcedZip64_IsReadable()
    {
        var raw = RawFile();
        var zip = _dir.File("big.zip");
        ExternalTool.Run("zip", ["-q", "-fz", "-j", zip, raw], null, null, null);

        using var stream = CompressedImageStream.Open(zip);

        Assert.Equal(Payload.Length, stream.UncompressedLength);
        AssertSame(Payload, ReadAll(stream));
    }

    [ToolFact("zip")]
    public void Zip_WithSeveralFiles_ChoosesTheImageThenTheLargestFile()
    {
        var zip = _dir.File("many.zip");
        var folder = Path.Combine(_dir.Path, "content");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "README.txt"), new string('x', 50_000));
        File.WriteAllBytes(Path.Combine(folder, "tool.iso"), Payload.AsSpan(0, 20_000).ToArray());
        File.WriteAllText(Path.Combine(folder, "tool.iso.sha256"), "00");
        ExternalTool.Run("zip", ["-q", "-r", zip, "."], folder, null, null);

        using var chosen = CompressedImageStream.Open(zip);
        Assert.Equal("tool.iso", chosen.EntryName);
        Assert.Equal(3, chosen.Entries.Count);

        using var named = CompressedImageStream.Open(zip, new CompressedImageOptions { EntryName = "readme.txt" });
        Assert.Equal("README.txt", named.EntryName);

        var plain = _dir.File("plain.zip");
        ExternalTool.Run("zip", ["-q", plain, "README.txt", "tool.iso.sha256"], folder, null, null);
        using var largest = CompressedImageStream.Open(plain);
        Assert.Equal("README.txt", largest.EntryName);
    }

    [ToolFact("zip")]
    public void Zip_MacResourceForksAreIgnored()
    {
        var folder = Path.Combine(_dir.Path, "mac");
        Directory.CreateDirectory(Path.Combine(folder, "__MACOSX"));
        File.WriteAllBytes(Path.Combine(folder, "disk.img"), Payload.AsSpan(0, 10_000).ToArray());
        File.WriteAllBytes(Path.Combine(folder, "__MACOSX", "._disk.img"), new byte[500_000]);
        var zip = _dir.File("mac.zip");
        ExternalTool.Run("zip", ["-q", "-r", zip, "."], folder, null, null);

        using var stream = CompressedImageStream.Open(zip);

        Assert.Equal("disk.img", stream.EntryName);
        Assert.Single(stream.Entries);
    }

    [ToolFact("zip")]
    public void Zip_UnknownEntryName_IsRejected()
    {
        var raw = RawFile();
        var zip = _dir.File("one.zip");
        ExternalTool.Run("zip", ["-q", "-j", zip, raw], null, null, null);

        var ex = Assert.Throws<BootrixException>(() =>
            CompressedImageStream.Open(zip, new CompressedImageOptions { EntryName = "missing.iso" }));

        Assert.Equal(ErrorCode.ImageUnreadable, ex.Code);
    }

    [ToolFact("zip")]
    public void Zip_PasswordProtectedEntry_IsReportedAsEncrypted()
    {
        var raw = RawFile();
        var zip = _dir.File("secret.zip");
        ExternalTool.Run("zip", ["-q", "-j", "-P", "secret", zip, raw], null, null, null);

        var ex = Assert.Throws<BootrixException>(() => CompressedImageStream.Open(zip));

        Assert.Equal(ErrorCode.ImageEncrypted, ex.Code);
    }

    [ToolTheory("xz")]
    [InlineData(0.5)]
    [InlineData(0.99)]
    public void Xz_Truncated_IsRefusedBeforeAnyDataIsRead(double keep)
    {
        var path = Compress("xz", ["-c", "-1"], "cut.xz");
        Truncate(path, keep);

        var ex = Assert.Throws<BootrixException>(() => CompressedImageStream.Open(path));

        Assert.Equal(ErrorCode.ImageUnreadable, ex.Code);
    }

    [ToolFact("bzip2")]
    public void BZip2_Truncated_IsRefusedBeforeAnyDataIsRead()
    {
        var path = Compress("bzip2", ["-c"], "cut.bz2");
        Truncate(path, 0.8);

        var ex = Assert.Throws<BootrixException>(() => CompressedImageStream.Open(path));

        Assert.Equal(ErrorCode.ImageUnreadable, ex.Code);
    }

    [ToolFact("zstd")]
    public void Zstd_Truncated_IsRefusedBeforeAnyDataIsRead()
    {
        var path = Compress("zstd", ["-c", "-q"], "cut.zst");
        Truncate(path, 0.7);

        var ex = Assert.Throws<BootrixException>(() => CompressedImageStream.Open(path));

        Assert.Equal(ErrorCode.ImageUnreadable, ex.Code);
    }

    [ToolFact("zip")]
    public void Zip_Truncated_IsRefused()
    {
        var raw = RawFile();
        var zip = _dir.File("cut.zip");
        ExternalTool.Run("zip", ["-q", "-j", zip, raw], null, null, null);
        Truncate(zip, 0.9);

        var ex = Assert.Throws<BootrixException>(() => CompressedImageStream.Open(zip));

        Assert.Equal(ErrorCode.ImageUnreadable, ex.Code);
    }

    [ToolFact("gzip")]
    public void GZip_Truncated_FailsWhileReading()
    {
        var path = Compress("gzip", ["-c"], "cut.gz");
        Truncate(path, 0.6);

        using var stream = CompressedImageStream.Open(path);
        var ex = Assert.Throws<BootrixException>(() => ReadAll(stream));

        Assert.Equal(ErrorCode.ImageUnreadable, ex.Code);
    }

    [ToolFact("xz")]
    public void Lzma_TruncatedWithoutEndMarker_FailsWhileReading()
    {
        var path = Compress("xz", ["-c", "--format=lzma", "-1"], "cut.lzma");
        Truncate(path, 0.6);

        using var stream = CompressedImageStream.Open(path);

        Assert.Throws<BootrixException>(() => ReadAll(stream));
    }

    [ToolFact("xz")]
    public void Xz_OpenedWithoutStructureCheck_StillReadsTheIntactPart()
    {
        var path = Compress("xz", ["-c", "-1", "-T2", "--block-size=512KiB"], "partial.xz");
        Truncate(path, 0.5);

        using var stream = CompressedImageStream.Open(path, new CompressedImageOptions { CheckStructure = false });
        var buffer = new byte[100_000];

        Assert.Equal(buffer.Length, stream.ReadAtLeast(buffer, buffer.Length));
        Assert.Equal(Payload.AsSpan(0, buffer.Length).ToArray(), buffer);
    }

    [ToolTheory("gzip", "xz", "bzip2", "zstd", "compress")]
    [InlineData("gzip")]
    [InlineData("xz")]
    [InlineData("bzip2")]
    [InlineData("zstd")]
    [InlineData("compress")]
    public void NonSeekableSource_IsDecodedWithoutRewinding(string tool)
    {
        var extension = tool switch { "gzip" => "gz", "xz" => "xz", "bzip2" => "bz2", "zstd" => "zst", _ => "Z" };
        var path = Compress(tool, ["-c"], "stream." + extension);

        using var source = new ForwardOnlyStream(File.ReadAllBytes(path));
        using var stream = CompressedImageStream.Open(source);

        AssertSame(Payload, ReadAll(stream));
        Assert.Null(stream.UncompressedLength);
    }

    [Fact]
    public void UnknownData_IsRejectedAsUnsupported()
    {
        var path = RawFile(new byte[4096]);

        var ex = Assert.Throws<BootrixException>(() => CompressedImageStream.Open(path));

        Assert.Equal(ErrorCode.ImageUnsupported, ex.Code);
    }

    [Fact]
    public async Task OpenAsync_HonoursCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CompressedImageStream.OpenAsync(RawFile(), null, cts.Token));
    }

    private static void Truncate(string path, double keep)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Write);
        file.SetLength((long)(file.Length * keep));
    }

    private sealed class ForwardOnlyStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
