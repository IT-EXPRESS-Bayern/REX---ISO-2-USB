// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Net;

/// <summary>Half-open byte interval <c>[Start, End)</c>.</summary>
internal readonly record struct ByteRange(long Start, long End)
{
    public long Length => End - Start;
}

/// <summary>Sorted, non-overlapping and non-adjacent byte intervals; used for "which parts of the file are on disk".</summary>
internal sealed class RangeSet
{
    private readonly List<ByteRange> _ranges = [];

    public IReadOnlyList<ByteRange> Ranges => _ranges;

    public long Total { get; private set; }

    public static RangeSet From(IEnumerable<ByteRange> ranges)
    {
        var set = new RangeSet();
        foreach (var range in ranges)
        {
            set.Add(range.Start, range.End);
        }

        return set;
    }

    public RangeSet Clone() => From(_ranges);

    public void Add(long start, long end)
    {
        if (end <= start)
        {
            return;
        }

        var merged = new ByteRange(start, end);
        var index = 0;
        while (index < _ranges.Count && _ranges[index].End < merged.Start)
        {
            index++;
        }

        // Everything that overlaps or touches the new interval collapses into it.
        while (index < _ranges.Count && _ranges[index].Start <= merged.End)
        {
            merged = new ByteRange(Math.Min(merged.Start, _ranges[index].Start), Math.Max(merged.End, _ranges[index].End));
            Total -= _ranges[index].Length;
            _ranges.RemoveAt(index);
        }

        _ranges.Insert(index, merged);
        Total += merged.Length;
    }

    public void Remove(long start, long end)
    {
        if (end <= start)
        {
            return;
        }

        for (var i = 0; i < _ranges.Count;)
        {
            var range = _ranges[i];
            if (range.End <= start || range.Start >= end)
            {
                i++;
                continue;
            }

            _ranges.RemoveAt(i);
            Total -= range.Length;

            if (range.Start < start)
            {
                _ranges.Insert(i, new ByteRange(range.Start, start));
                Total += start - range.Start;
                i++;
            }

            if (range.End > end)
            {
                _ranges.Insert(i, new ByteRange(end, range.End));
                Total += range.End - end;
                i++;
            }
        }
    }

    public bool Covers(long start, long end)
    {
        if (end <= start)
        {
            return true;
        }

        foreach (var range in _ranges)
        {
            if (range.Start <= start && range.End >= end)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The parts of <c>[0, length)</c> that are not in the set.</summary>
    public IEnumerable<ByteRange> Gaps(long length)
    {
        long position = 0;
        foreach (var range in _ranges)
        {
            if (range.Start >= length)
            {
                break;
            }

            if (range.Start > position)
            {
                yield return new ByteRange(position, range.Start);
            }

            position = Math.Max(position, range.End);
        }

        if (position < length)
        {
            yield return new ByteRange(position, length);
        }
    }
}
