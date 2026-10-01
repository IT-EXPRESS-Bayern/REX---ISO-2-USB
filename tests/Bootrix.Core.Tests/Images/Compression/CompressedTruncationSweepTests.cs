// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Images.Compression;
using Bootrix.Core.Tests.Images.Support;

namespace Bootrix.Core.Tests.Images.Compression;

/// <summary>
/// A cut-off download must never pass as a complete image: wherever the file ends, opening or reading it has to
/// fail with a <see cref="BootrixException"/> rather than hand out a shorter image without complaint.
/// </summary>
public sealed class CompressedTruncationSweepTests : IDisposable
{
    private readonly TestDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private byte[] Compressed(string tool, string[] arguments)
    {
        var raw = _dir.Write("raw.img", TestDirectory.Compressible(700_000));
        var target = _dir.File("out.bin");
        ReferenceTool.Run(tool, arguments, _dir.Path, raw, target);
        return File.ReadAllBytes(target);
    }

    /// <summary>The first and last bytes (headers, footers, trailers) plus a spread of positions in between.</summary>
    private static IEnumerable<int> CutPoints(int length)
    {
        var points = new SortedSet<int>();
        for (var i = CompressionSniffer.HeaderLength; i < CompressionSniffer.HeaderLength + 24; i++)
        {
            points.Add(i);
        }

        for (var i = 1; i <= 40; i++)
        {
            points.Add(length - i);
        }

        var random = new Random(length);
        for (var i = 0; i < 80; i++)
        {
            points.Add(random.Next(CompressionSniffer.HeaderLength, length));
        }

        return points.Where(p => p >= CompressionSniffer.HeaderLength && p < length);
    }

    private static void AssertEveryCutIsRefused(byte[] complete, bool checkStructure, IEnumerable<int>? extraCuts = null)
    {
        // The untouched file must be accepted, otherwise the sweep proves nothing.
        using (var whole = CompressedImageStream.Open(new MemoryStream(complete), new CompressedImageOptions { CheckStructure = checkStructure }))
        {
            whole.CopyTo(Stream.Null);
        }

        foreach (var cut in CutPoints(complete.Length).Concat(extraCuts ?? []).Where(c => c < complete.Length).Distinct())
        {
            var refused = false;
            try
            {
                using var stream = CompressedImageStream.Open(new MemoryStream(complete, 0, cut), new CompressedImageOptions { CheckStructure = checkStructure });
                stream.CopyTo(Stream.Null);
            }
            catch (BootrixException)
            {
                refused = true;
            }

            Assert.True(refused, $"a file cut at {cut} of {complete.Length} bytes was accepted");
        }
    }

    [ToolTheory("xz")]
    [InlineData(true)]
    [InlineData(false)]
    public void Xz_CutAnywhere_IsRefused(bool checkStructure) =>
        AssertEveryCutIsRefused(Compressed("xz", ["-c", "-1"]), checkStructure);

    [ToolTheory("xz")]
    [InlineData(true)]
    [InlineData(false)]
    public void XzWithSeveralBlocks_CutAnywhere_IsRefused(bool checkStructure) =>
        AssertEveryCutIsRefused(Compressed("xz", ["-c", "-1", "--block-size=100KiB"]), checkStructure);

    /// <summary>
    /// Incompressible data makes xz store 64 KiB chunks uncompressed. A file that ends exactly where a chunk header
    /// should start made SharpCompress' LZMA2 reader loop forever, so the area around the chunk boundaries is swept byte by byte.
    /// </summary>
    [ToolTheory("xz")]
    [InlineData(true)]
    [InlineData(false)]
    public void XzOfIncompressibleData_CutNearChunkBoundaries_IsRefused(bool checkStructure)
    {
        var random = new byte[300_000];
        new Random(11).NextBytes(random);
        var raw = _dir.Write("random.img", random);
        var target = _dir.File("random.xz");
        ReferenceTool.Run("xz", ["-c", "-1"], _dir.Path, raw, target);
        var complete = File.ReadAllBytes(target);

        // Stream header (12 bytes) and block header come first; each stored chunk is a 3-byte header plus 65536 bytes.
        var cuts = Enumerable.Range(16, 40)
            .Concat(Enumerable.Range(1, 4).SelectMany(chunk => Enumerable.Range(24 + (chunk * 65_539) - 20, 40)));

        AssertEveryCutIsRefused(complete, checkStructure, cuts);
    }

    [ToolTheory("zstd")]
    [InlineData(true)]
    [InlineData(false)]
    public void Zstd_CutAnywhere_IsRefused(bool checkStructure) =>
        AssertEveryCutIsRefused(Compressed("zstd", ["-c", "-q", "-1"]), checkStructure);

    [ToolTheory("bzip2")]
    [InlineData(true)]
    [InlineData(false)]
    public void BZip2_CutAnywhere_IsRefused(bool checkStructure) =>
        AssertEveryCutIsRefused(Compressed("bzip2", ["-c", "-1"]), checkStructure);

    [ToolTheory("gzip")]
    [InlineData(true)]
    [InlineData(false)]
    public void GZip_CutAnywhere_IsRefused(bool checkStructure) =>
        AssertEveryCutIsRefused(Compressed("gzip", ["-c", "-1"]), checkStructure);

    [ToolTheory("xz")]
    [InlineData(true)]
    [InlineData(false)]
    public void LzmaAlone_CutAnywhere_IsRefused(bool checkStructure) =>
        AssertEveryCutIsRefused(Compressed("xz", ["-c", "--format=lzma", "-1"]), checkStructure);
}
