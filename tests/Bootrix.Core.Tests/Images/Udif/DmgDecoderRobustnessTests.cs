// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Bootrix.Core.Errors;
using Bootrix.Core.Images.Udif;

namespace Bootrix.Core.Tests.Images.Udif;

/// <summary>Damaged compressed data and mutated images: only controlled errors, no hangs, no runaway allocations.</summary>
public class DmgDecoderRobustnessTests
{
    // BOOTRIX_FUZZ_ITERATIONS raises the number of mutations for longer local runs.
    private static readonly int Iterations =
        int.TryParse(Environment.GetEnvironmentVariable("BOOTRIX_FUZZ_ITERATIONS"), out var configured) && configured > 0 ? configured : 250;
    private static readonly TimeSpan IterationTimeout = TimeSpan.FromSeconds(20);

    public static TheoryData<string> CodecNames => ["zlib", "bzip2", "adc", "lzfse", "xz"];

    private static (ChunkSpec Chunk, byte[] Plain) SampleChunk(string codec)
    {
        var plain = ImageTestData.Text(64 * 512, 1234);
        return codec switch
        {
            "zlib" => (ChunkSpec.Zlib(plain), plain),
            "bzip2" => (ChunkSpec.Bzip2(plain), plain),
            "adc" => (ChunkSpec.Adc(plain), plain),
            "lzfse" => (CodecFixtures.Lzfse.Single(f => f.Name == "lzfse_text_128k").ToChunk(), CodecFixtures.Lzfse.Single(f => f.Name == "lzfse_text_128k").Plain()),
            "xz" => (CodecFixtures.Xz.Single(f => f.Name == "xz_text_128k_crc64").ToChunk(), CodecFixtures.Xz.Single(f => f.Name == "xz_text_128k_crc64").Plain()),
            _ => throw new ArgumentOutOfRangeException(nameof(codec)),
        };
    }

    private static byte[] ImageOf(ChunkSpec chunk) =>
        new UdifBuilder().AddPartition(0, "disk image (Apple_HFS : 1)", 0, [chunk]).Build();

    // A mutated sector count can legitimately describe terabytes of zeros, which nobody wants to read to the end in a test.
    private static readonly DmgReaderOptions FuzzOptions = new() { MaxVolumeBytes = 256L * 1024 * 1024 };

    // Runs the whole open-and-read cycle; returns the exception it ended with, if any.
    private static Exception? Attempt(byte[] image, DmgReaderOptions? options = null)
    {
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            using var reader = DmgReader.Open(new MemoryStream(image), options: options ?? FuzzOptions);
            reader.CopyTo(Stream.Null);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
        finally
        {
            Assert.True(GC.GetAllocatedBytesForCurrentThread() - allocatedBefore < 512L * 1024 * 1024, "runaway allocation");
        }
    }

    private static void AssertControlled(byte[] image, string description, DmgReaderOptions? options = null)
    {
        var task = Task.Run(() => Attempt(image, options));
        Assert.True(task.Wait(IterationTimeout), "timeout for " + description);

        var exception = task.Result;
        Assert.True(exception is null or BootrixException, $"{description}: unexpected {exception}");
    }

    [Theory]
    [MemberData(nameof(CodecNames))]
    public void ByteFlipsInsideTheCompressedChunk_NeverEscapeAsOtherExceptions(string codec)
    {
        var (chunk, _) = SampleChunk(codec);
        var image = ImageOf(chunk);
        var random = new Random(77);

        for (var i = 0; i < Iterations; i++)
        {
            var damaged = (byte[])image.Clone();
            for (var flips = random.Next(1, 4); flips > 0; flips--)
            {
                damaged[random.Next(0, chunk.Stored.Length)] ^= (byte)random.Next(1, 256);
            }

            AssertControlled(damaged, $"{codec} flip iteration {i}");
        }
    }

    [Theory]
    [MemberData(nameof(CodecNames))]
    public void TruncatedCompressedChunk_IsCorruption(string codec)
    {
        var (chunk, _) = SampleChunk(codec);
        var image = new UdifBuilder
        {
            MutateTable = (_, table) =>
            {
                var stored = BinaryPrimitives.ReadUInt64BigEndian(table.AsSpan(0xCC + 32));
                BinaryPrimitives.WriteUInt64BigEndian(table.AsSpan(0xCC + 32), stored / 2);
                return table;
            },
        }.AddPartition(0, "disk image (Apple_HFS : 1)", 0, [chunk]).Build();

        var ex = Assert.IsType<BootrixException>(Attempt(image));

        Assert.Equal(ErrorCode.ImageCorrupt, ex.Code);
    }

    [Theory]
    [MemberData(nameof(CodecNames))]
    public void RandomBytesAsChunk_AreCorruption(string codec)
    {
        var (chunk, plain) = SampleChunk(codec);
        var type = chunk.Type;
        var garbage = ImageTestData.Random(Math.Max(chunk.Stored.Length, 1000), 5);
        var image = ImageOf(ChunkSpec.Precompressed(type, garbage, plain));

        var ex = Assert.IsType<BootrixException>(Attempt(image));

        Assert.Equal(ErrorCode.ImageCorrupt, ex.Code);
    }

    [Theory]
    [MemberData(nameof(CodecNames))]
    public void ChunkDecodingToWrongSize_IsCorruption(string codec)
    {
        var (chunk, _) = SampleChunk(codec);
        // Same stored bytes, but the table claims one sector more or less than they decode to.
        foreach (var delta in new[] { -1, 1 })
        {
            var image = new UdifBuilder
            {
                MutateTable = (_, table) =>
                {
                    var sectors = BinaryPrimitives.ReadUInt64BigEndian(table.AsSpan(0xCC + 16));
                    BinaryPrimitives.WriteUInt64BigEndian(table.AsSpan(0xCC + 16), (ulong)((long)sectors + delta));
                    BinaryPrimitives.WriteUInt64BigEndian(table.AsSpan(0x10), (ulong)((long)sectors + delta));
                    return table;
                },
            }.AddPartition(0, "disk image (Apple_HFS : 1)", 0, [chunk]).Build();

            var ex = Assert.IsType<BootrixException>(Attempt(image));

            Assert.Equal(ErrorCode.ImageCorrupt, ex.Code);
        }
    }

    [Fact]
    public void Adc_MatchReachingBeforeTheStart_IsCorruption()
    {
        // 2-byte match: length 3, distance field 5, but nothing has been written yet.
        byte[] stored = [0x00, 0x05];
        var image = ImageOf(ChunkSpec.Precompressed(UdifChunkType.Adc, stored, new byte[512]));

        Assert.Equal(ErrorCode.ImageCorrupt, Assert.IsType<BootrixException>(Attempt(image)).Code);
    }

    [Fact]
    public void Adc_LiteralRunLongerThanTheInput_IsCorruption()
    {
        byte[] stored = [0xFF, 1, 2, 3];
        var image = ImageOf(ChunkSpec.Precompressed(UdifChunkType.Adc, stored, new byte[512]));

        Assert.Equal(ErrorCode.ImageCorrupt, Assert.IsType<BootrixException>(Attempt(image)).Code);
    }

    [Fact]
    public void Adc_MatchLongerThanTheRemainingOutput_IsCorruption()
    {
        // 504 literal bytes fill all but 8 bytes of the 512-byte chunk; the following 67-byte match does not fit.
        var stored = new List<byte>();
        foreach (var run in new[] { 128, 128, 128, 120 })
        {
            stored.Add((byte)(0x80 | (run - 1)));
            stored.AddRange(Enumerable.Repeat((byte)0x11, run));
        }

        stored.AddRange([0x40 | 63, 0x00, 0x00]);
        var image = ImageOf(ChunkSpec.Precompressed(UdifChunkType.Adc, [.. stored], new byte[512]));

        Assert.Equal(ErrorCode.ImageCorrupt, Assert.IsType<BootrixException>(Attempt(image)).Code);
    }

    [Fact]
    public void Xz_WithGigabyteDictionary_IsRefusedWithoutAllocatingIt()
    {
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var image = ImageOf(ChunkSpec.Precompressed(UdifChunkType.Xz, XzWithDictionaryByte(40), new byte[512]));

        var ex = Assert.IsType<BootrixException>(Attempt(image));

        Assert.Equal(ErrorCode.ImageUnsupported, ex.Code);
        Assert.True(GC.GetTotalAllocatedBytes(precise: true) - allocated < 64L * 1024 * 1024);
    }

    [Fact]
    public void Xz_WithDictionaryAtTheLimit_IsNotRefusedByTheGuard()
    {
        // Dictionary byte 24 stands for 8 MiB; the stream itself is bogus, so the failure is corruption, not a limit.
        var image = ImageOf(ChunkSpec.Precompressed(UdifChunkType.Xz, XzWithDictionaryByte(24), new byte[512]));

        var ex = Assert.IsType<BootrixException>(Attempt(image));

        Assert.Equal(ErrorCode.ImageCorrupt, ex.Code);
    }

    [Fact]
    public void ReadAheadWithDamagedChunkInTheMiddle_ReportsTheErrorWhenThatChunkIsRead()
    {
        var plain = ImageTestData.Text(16 * 512, 2);
        var chunks = Enumerable.Range(0, 12).Select(i => i == 6
            ? ChunkSpec.Precompressed(UdifChunkType.Zlib, ImageTestData.Random(200, 1), plain)
            : ChunkSpec.Zlib(plain)).ToList();
        var image = new UdifBuilder().AddPartition(0, "x (Apple_HFS : 1)", 0, chunks).Build();

        using var reader = DmgReader.Open(new MemoryStream(image), options: new DmgReaderOptions { ReadAhead = 4 });
        var buffer = new byte[16 * 512];
        for (var i = 0; i < 6; i++)
        {
            reader.ReadExactly(buffer);
            Assert.Equal(plain, buffer);
        }

        Assert.Equal(ErrorCode.ImageCorrupt, Assert.Throws<BootrixException>(() => reader.ReadExactly(buffer)).Code);
    }

    [Fact]
    public void MutatedBlockTables_NeverEscapeAsOtherExceptions()
    {
        var random = new Random(4242);
        for (var i = 0; i < Iterations; i++)
        {
            var seed = random.Next();
            var image = new UdifBuilder
            {
                MutateTable = (_, table) =>
                {
                    var local = new Random(seed);
                    for (var flips = local.Next(1, 5); flips > 0; flips--)
                    {
                        var position = local.Next(0, Math.Min(table.Length, 0xCC + (6 * 40)));
                        table[position] = local.Next(4) == 0 ? (byte)0xFF : (byte)local.Next(256);
                    }

                    return table;
                },
            }.AddPartition(0, "disk image (Apple_HFS : 1)", 0, Chunks8()).Build();

            AssertControlled(image, $"table mutation seed {seed}");
        }
    }

    [Theory]
    [InlineData("zlib")]
    [InlineData("mixed")]
    public void MutatedBytesAnywhereInTheImage_NeverEscapeAsOtherExceptions(string codec)
    {
        var image = DmgFixtures.Build(codec, chunkSectors: 128);
        var random = new Random(31337);

        for (var i = 0; i < Iterations; i++)
        {
            var damaged = (byte[])image.Clone();
            for (var flips = random.Next(1, 5); flips > 0; flips--)
            {
                // Half of the hits land in the trailer and property list at the end, where the structure is.
                var position = random.Next(2) == 0 ? random.Next(Math.Max(0, image.Length - 4096), image.Length) : random.Next(image.Length);
                damaged[position] = (byte)random.Next(256);
            }

            AssertControlled(damaged, $"{codec} mutation iteration {i}");
        }
    }

    private static List<ChunkSpec> Chunks8()
    {
        var plain = ImageTestData.Text(8 * 512, 5);
        return [ChunkSpec.Raw(plain), ChunkSpec.Zlib(plain), ChunkSpec.Zero(8), ChunkSpec.Bzip2(plain), ChunkSpec.Adc(plain), ChunkSpec.Ignore(8)];
    }

    // A structurally valid XZ container (empty payload) whose single block announces the given LZMA2 dictionary.
    private static byte[] XzWithDictionaryByte(byte dictionary)
    {
        var xz = new byte[56];
        byte[] header = [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00, 0x00, 0x04];
        header.CopyTo(xz, 0);
        xz[12] = 0x02; // block header of 12 bytes
        xz[13] = 0x00; // one filter, no sizes
        xz[14] = 0x21; // LZMA2
        xz[15] = 0x01;
        xz[16] = dictionary;
        xz[36] = 0x00; // index
        xz[37] = 0x01; // one record
        xz[38] = 24; // unpadded size
        xz[39] = 0x00;
        xz[48] = 0x01; // backward size: (1 + 1) * 4 = 8 bytes of index
        xz[54] = (byte)'Y';
        xz[55] = (byte)'Z';
        return xz;
    }
}
