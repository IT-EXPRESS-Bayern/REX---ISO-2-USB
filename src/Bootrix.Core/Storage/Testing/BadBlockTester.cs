// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Storage.Testing;

public readonly record struct BadRange(long Offset, long Length);

public sealed record BadBlockResult(int Passes, IReadOnlyList<BadRange> BadRanges, int ReadErrors, int WriteErrors, long BytesTested)
{
    public bool IsClean => BadRanges.Count == 0 && ReadErrors == 0 && WriteErrors == 0;
}

/// <summary>
/// Writes test patterns over the whole device and compares them on read-back. The pattern set
/// follows the classic badblocks routine; flash media get the longer set because a single pass
/// of zeros says little about a worn cell.
/// </summary>
public static class BadBlockTester
{
    public static readonly byte[] QuickPatterns = [0x55, 0xAA];
    public static readonly byte[] ThoroughPatterns = [0x55, 0xAA, 0xFF, 0x00];

    public static Task<BadBlockResult> RunAsync(
        IBlockDevice device,
        IReadOnlyList<byte>? patterns = null,
        int chunkBytes = 1024 * 1024,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Run(device, patterns ?? ThoroughPatterns, chunkBytes, progress, cancellationToken), cancellationToken);
    }

    private static BadBlockResult Run(
        IBlockDevice device,
        IReadOnlyList<byte> patterns,
        int chunkBytes,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var sector = device.SectorSize;
        chunkBytes = chunkBytes / sector * sector;
        var length = device.Length / sector * sector;
        using var expected = new AlignedBuffer(chunkBytes, device.BufferAlignment);
        using var actual = new AlignedBuffer(chunkBytes, device.BufferAlignment);

        var bad = new List<BadRange>();
        var readErrors = 0;
        var writeErrors = 0;
        var passIndex = 0;
        var totalWork = (double)length * patterns.Count * 2;
        double done = 0;

        foreach (var pattern in patterns)
        {
            expected.GetSpan().Fill(pattern);

            for (long offset = 0; offset < length; offset += chunkBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var take = (int)Math.Min(chunkBytes, length - offset);
                try
                {
                    device.Write(offset, expected.GetSpan()[..take]);
                }
                catch (IOException)
                {
                    writeErrors++;
                    AddRange(bad, offset, take);
                }

                done += take;
                progress?.Report(done / totalWork);
            }

            device.Flush();

            for (long offset = 0; offset < length; offset += chunkBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var take = (int)Math.Min(chunkBytes, length - offset);
                try
                {
                    var read = device.Read(offset, actual.GetSpan()[..take]);
                    if (read < take || !actual.GetSpan()[..take].SequenceEqual(expected.GetSpan()[..take]))
                    {
                        AddMismatches(bad, device, offset, actual.GetSpan()[..take], expected.GetSpan()[..take], sector);
                    }
                }
                catch (IOException)
                {
                    readErrors++;
                    AddRange(bad, offset, take);
                }

                done += take;
                progress?.Report(done / totalWork);
            }

            passIndex++;
        }

        return new BadBlockResult(passIndex, Merge(bad), readErrors, writeErrors, length);
    }

    /// <summary>Narrows a mismatching chunk down to the sectors that actually differ.</summary>
    private static void AddMismatches(List<BadRange> bad, IBlockDevice device, long chunkOffset, ReadOnlySpan<byte> actual, ReadOnlySpan<byte> expected, int sector)
    {
        _ = device;
        for (var i = 0; i + sector <= actual.Length; i += sector)
        {
            if (!actual.Slice(i, sector).SequenceEqual(expected.Slice(i, sector)))
            {
                AddRange(bad, chunkOffset + i, sector);
            }
        }
    }

    private static void AddRange(List<BadRange> ranges, long offset, long length) => ranges.Add(new BadRange(offset, length));

    private static List<BadRange> Merge(List<BadRange> ranges)
    {
        var merged = new List<BadRange>();
        foreach (var range in ranges.OrderBy(r => r.Offset))
        {
            if (merged.Count > 0 && merged[^1].Offset + merged[^1].Length >= range.Offset)
            {
                var last = merged[^1];
                var end = Math.Max(last.Offset + last.Length, range.Offset + range.Length);
                merged[^1] = new BadRange(last.Offset, end - last.Offset);
            }
            else
            {
                merged.Add(range);
            }
        }

        return merged;
    }
}
