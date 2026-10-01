// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Bootrix.Core.Catalog.Microsoft;
using Bootrix.Core.Tests.Net.Support;
using Bootrix.Core.Tests.Tooling;
using Xunit.Abstractions;

namespace Bootrix.Core.Tests.Catalog.Microsoft;

/// <summary>
/// The real catalogs consist of a single verbatim block. These tests feed the decoder what other encoders may send:
/// aligned blocks, uncompressed blocks between coded ones, repeated offsets, far matches, every window size. The test
/// encoder is independent of the decoder; 7-Zip, which has its own LZX decoder, reads the same cabinets.
/// </summary>
public class LzxRoundTripTests(ITestOutputHelper output)
{
    private static readonly string[] Words =
    [
        "Windows", "catalog", "Edition", "Professional", "language", "architecture", "download", "<File>", "</File>", "x64",
        "ClientConsumer", "release", "Sha1", "Size", "FilePath", "http://dl.delivery.mp.microsoft.com/filestreamingservice/files/",
    ];

    /// <summary>Text, noise, a long run, short periods, counters, and a copy of early noise placed far behind it.</summary>
    private static byte[] Corpus(int farDistance)
    {
        var random = new Random(2026);
        using var stream = new MemoryStream();

        var early = new byte[4000];
        random.NextBytes(early);
        stream.Write(early);

        void Text(int bytes)
        {
            var written = 0;
            while (written < bytes)
            {
                var word = Encoding.ASCII.GetBytes(Words[random.Next(Words.Length)] + (random.Next(4) == 0 ? "\n" : " "));
                stream.Write(word);
                written += word.Length;
            }
        }

        Text(20_000);
        stream.Write(Enumerable.Repeat((byte)'=', 5000).ToArray());
        stream.Write(Enumerable.Range(0, 5000).Select(i => (byte)("abcdefg"[i % 7])).ToArray());
        stream.Write(Enumerable.Range(0, 3000).SelectMany(i => BitConverter.GetBytes(i * 17)).ToArray());
        var noise = new byte[20_000];
        random.NextBytes(noise);
        stream.Write(noise);

        while (stream.Length < farDistance)
        {
            Text(10_000);
        }

        stream.Write(early);
        Text(30_000);
        return stream.ToArray();
    }

    private static byte[] Decode(byte[] cabinet)
    {
        var archive = CabinetArchive.Parse(cabinet);
        return archive.Extract(Assert.Single(archive.Entries));
    }

    private static byte[] Cabinet(LzxTestEncoder encoder, byte[] data, int windowBits, params (LzxBlockKind Kind, int Length)[] blocks)
    {
        var frames = encoder.Encode(data, blocks);
        return new CabinetBuilder()
            .Folder(CabinetBuilder.Lzx(windowBits), [.. frames])
            .File("data.bin", 0, 0, (uint)data.Length)
            .Build();
    }

    private void CheckAgainstSevenZip(byte[] cabinet, byte[] expected)
    {
        if (!ExternalTools.IsAvailable("7z"))
        {
            output.WriteLine("7z is not installed; only the decoder under test was checked.");
            return;
        }

        using var directory = new TempDirectory();
        File.WriteAllBytes(directory.File("test.cab"), cabinet);
        var result = ExternalTools.Run("7z", "x", "-y", $"-o{directory.Path}", directory.File("test.cab"));

        Assert.True(result.ExitCode == 0, "7-Zip rejected the cabinet: " + result.Combined);
        Assert.True(expected.AsSpan().SequenceEqual(File.ReadAllBytes(directory.File("data.bin"))), "7-Zip's output differs from the source data");
    }

    private LzxEncoderStatistics Verify(int windowBits, byte[] data, params (LzxBlockKind Kind, int Length)[] blocks) =>
        Verify(new LzxTestEncoder(windowBits), windowBits, data, blocks);

    private LzxEncoderStatistics Verify(LzxTestEncoder encoder, int windowBits, byte[] data, params (LzxBlockKind Kind, int Length)[] blocks)
    {
        var cabinet = Cabinet(encoder, data, windowBits, blocks);

        Assert.True(data.AsSpan().SequenceEqual(Decode(cabinet)), "decoder output differs from the source data");
        CheckAgainstSevenZip(cabinet, data);
        output.WriteLine($"{encoder.Statistics.Literals} literals, {encoder.Statistics.Matches} matches ({encoder.Statistics.RepeatedOffsetMatches} repeated offsets, {encoder.Statistics.LongMatches} long, {encoder.Statistics.AlignedOffsets} aligned, {encoder.Statistics.FarMatches} far)");
        return encoder.Statistics;
    }

    [Theory]
    [InlineData(LzxBlockKind.Verbatim)]
    [InlineData(LzxBlockKind.Aligned)]
    public void SingleBlock_OfEitherCodedKind_RoundTrips(LzxBlockKind kind)
    {
        var data = Corpus(20_000);

        var statistics = Verify(16, data, (kind, data.Length));

        Assert.True(statistics.RepeatedOffsetMatches > 0 && statistics.LongMatches > 0 && statistics.Literals > 0);
        Assert.Equal(kind == LzxBlockKind.Aligned, statistics.AlignedOffsets > 0);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(20)]
    [InlineData(21)]
    public void EveryWindowSize_RoundTripsWithBothCodedKinds(int windowBits)
    {
        var data = Corpus(20_000);
        var half = data.Length / 2;

        Verify(windowBits, data, (LzxBlockKind.Aligned, half), (LzxBlockKind.Verbatim, data.Length - half));
    }

    [Theory]
    [InlineData(LzxBlockKind.Verbatim, 19)]
    [InlineData(LzxBlockKind.Aligned, 19)]
    [InlineData(LzxBlockKind.Aligned, 21)]
    public void MatchesBeyondTheThirtySixthSlot_UseSeventeenExtraBits(LzxBlockKind kind, int windowBits)
    {
        // The copy of the early noise lies about 300 000 bytes back, which needs slot 36 or higher.
        var data = Corpus(300_000);

        var statistics = Verify(windowBits, data, (kind, data.Length));

        Assert.True(statistics.FarMatches > 0, "the data holds no match that far back");
    }

    [Fact]
    public void BlocksOfAllKindsInOneFolder_CarryTreesRepeatedOffsetsAndAlignmentAcrossFrames()
    {
        var data = Corpus(130_000);
        var blocks = new[]
        {
            (LzxBlockKind.Verbatim, 50_001),
            (LzxBlockKind.Uncompressed, 40_001),
            (LzxBlockKind.Aligned, 30_000),
            (LzxBlockKind.Uncompressed, 1_000),
            (LzxBlockKind.Verbatim, data.Length - 121_002),
        };

        Verify(17, data, blocks);
    }

    [Fact]
    public void UncompressedBlocksOnly_RoundTrip()
    {
        var data = Corpus(60_000)[..100_000];

        Verify(15, data, (LzxBlockKind.Uncompressed, 33_333), (LzxBlockKind.Uncompressed, 33_333), (LzxBlockKind.Uncompressed, 33_334));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OddUncompressedBlockEndingAtAFrameEnd_WithThePaddingByteInEitherFrame_IsReadTheSame(bool paddingStartsNextFrame)
    {
        var data = Corpus(20_000)[..70_000];
        var blocks = new[] { (LzxBlockKind.Uncompressed, 1), (LzxBlockKind.Uncompressed, 32_767), (LzxBlockKind.Verbatim, 37_232) };

        Verify(new LzxTestEncoder(16, paddingStartsNextFrame), 16, data, blocks);
    }
}
