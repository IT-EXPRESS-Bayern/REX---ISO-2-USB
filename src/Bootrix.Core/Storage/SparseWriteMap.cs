// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Storage;

public readonly record struct ByteRange(long Start, long Length)
{
    public long End => Start + Length;
}

/// <summary>
/// The parts of an image that actually have to reach the target, as found in a block map next to the image.
/// What lies between the ranges is not written: the target keeps what it had there, unless
/// <see cref="FillGaps"/> asks for zeros. Image formats whose unmapped blocks are never read (file system
/// images with lazily initialised tables) are the reason this is safe at all.
/// </summary>
public sealed class SparseWriteMap
{
    public SparseWriteMap(IEnumerable<ByteRange> ranges, bool fillGaps = false)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        Ranges = Normalize(ranges);
        FillGaps = fillGaps;
        MappedBytes = Ranges.Sum(range => range.Length);
    }

    /// <summary>Sorted, non-empty, non-overlapping and non-adjacent.</summary>
    public IReadOnlyList<ByteRange> Ranges { get; }

    /// <summary>Write zeros into the gaps, so the result no longer depends on what the target held before.</summary>
    public bool FillGaps { get; }

    public long MappedBytes { get; }

    /// <summary>
    /// The pieces of <c>[start, start + length)</c> to write, widened to multiples of <paramref name="alignment"/>
    /// (a device only takes whole sectors) and merged where the widening makes them touch.
    /// </summary>
    internal List<ByteRange> Select(long start, long length, int alignment)
    {
        var end = start + length;
        var result = new List<ByteRange>();
        var first = FindFirst(start);
        for (var i = first; i < Ranges.Count && Ranges[i].Start < end; i++)
        {
            var from = AlignDown(Math.Max(Ranges[i].Start, start), alignment);
            var to = AlignUp(Math.Min(Ranges[i].End, end), alignment);
            if (result.Count > 0 && from <= result[^1].End)
            {
                result[^1] = new ByteRange(result[^1].Start, Math.Max(result[^1].End, to) - result[^1].Start);
            }
            else
            {
                result.Add(new ByteRange(from, to - from));
            }
        }

        return result;
    }

    /// <summary>Zeroes the bytes of <paramref name="data"/> (which starts at image offset <paramref name="offset"/>) that no range covers.</summary>
    internal void ClearGaps(long offset, Span<byte> data)
    {
        var end = offset + data.Length;
        var cursor = offset;
        for (var i = FindFirst(offset); i < Ranges.Count && cursor < end; i++)
        {
            var range = Ranges[i];
            if (range.Start > cursor)
            {
                var gapEnd = Math.Min(range.Start, end);
                data[(int)(cursor - offset)..(int)(gapEnd - offset)].Clear();
            }

            cursor = Math.Max(cursor, range.End);
        }

        if (cursor < end)
        {
            data[(int)(cursor - offset)..].Clear();
        }
    }

    /// <summary>Index of the first range that ends after <paramref name="position"/>.</summary>
    private int FindFirst(long position)
    {
        int low = 0, high = Ranges.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (Ranges[middle].End <= position)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    private static List<ByteRange> Normalize(IEnumerable<ByteRange> ranges)
    {
        var sorted = ranges.Where(range => range.Length > 0).OrderBy(range => range.Start).ToList();
        var merged = new List<ByteRange>(sorted.Count);
        foreach (var range in sorted)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(range.Start);
            if (merged.Count > 0 && range.Start <= merged[^1].End)
            {
                merged[^1] = new ByteRange(merged[^1].Start, Math.Max(merged[^1].End, range.End) - merged[^1].Start);
            }
            else
            {
                merged.Add(range);
            }
        }

        return merged;
    }

    private static long AlignDown(long value, int alignment) => value / alignment * alignment;

    private static long AlignUp(long value, int alignment) => (value + alignment - 1) / alignment * alignment;
}
