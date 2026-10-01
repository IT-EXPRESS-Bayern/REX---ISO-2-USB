// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;

namespace Bootrix.Core.Optical.Reading;

public readonly record struct SectorRange(long Start, long Count)
{
    public long End => Start + Count;

    public override string ToString() => Count == 1 ? Start.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"{Start}-{End - 1}";
}

/// <summary>Sectors that could not be read, kept as merged ranges because defects are usually contiguous scratches.</summary>
public sealed class BadSectorMap
{
    private readonly List<SectorRange> _ranges = [];

    public BadSectorMap()
    {
    }

    public BadSectorMap(IEnumerable<SectorRange> ranges)
    {
        foreach (var range in ranges.Where(r => r.Count > 0).OrderBy(r => r.Start))
        {
            if (_ranges.Count > 0 && range.Start <= _ranges[^1].End)
            {
                var last = _ranges[^1];
                _ranges[^1] = last with { Count = Math.Max(last.End, range.End) - last.Start };
            }
            else
            {
                _ranges.Add(range);
            }
        }
    }

    public IReadOnlyList<SectorRange> Ranges => _ranges;

    public long Count => _ranges.Sum(r => r.Count);

    public bool IsEmpty => _ranges.Count == 0;

    /// <summary>Adds a sector. Sectors arrive in ascending order while ripping, so the common case only extends the last range.</summary>
    public void Add(long lba)
    {
        if (_ranges.Count == 0)
        {
            _ranges.Add(new SectorRange(lba, 1));
            return;
        }

        var last = _ranges[^1];
        if (lba == last.End)
        {
            _ranges[^1] = last with { Count = last.Count + 1 };
        }
        else if (lba > last.End)
        {
            _ranges.Add(new SectorRange(lba, 1));
        }
        else
        {
            InsertOutOfOrder(lba);
        }
    }

    public bool Contains(long lba)
    {
        var index = _ranges.BinarySearch(new SectorRange(lba, 1), RangeStartComparer.Instance);
        if (index >= 0)
        {
            return true;
        }

        var before = ~index - 1;
        return before >= 0 && lba < _ranges[before].End;
    }

    public string Describe()
    {
        var text = new StringBuilder();
        foreach (var range in _ranges)
        {
            if (text.Length > 0)
            {
                text.Append(", ");
            }

            text.Append(range);
        }

        return text.ToString();
    }

    private void InsertOutOfOrder(long lba)
    {
        var index = _ranges.BinarySearch(new SectorRange(lba, 1), RangeStartComparer.Instance);
        if (index >= 0)
        {
            return;
        }

        var next = ~index;
        var previous = next - 1;
        if (previous >= 0 && lba < _ranges[previous].End)
        {
            return;
        }

        var joinsPrevious = previous >= 0 && _ranges[previous].End == lba;
        var joinsNext = next < _ranges.Count && _ranges[next].Start == lba + 1;
        if (joinsPrevious && joinsNext)
        {
            _ranges[previous] = new SectorRange(_ranges[previous].Start, _ranges[previous].Count + 1 + _ranges[next].Count);
            _ranges.RemoveAt(next);
        }
        else if (joinsPrevious)
        {
            _ranges[previous] = _ranges[previous] with { Count = _ranges[previous].Count + 1 };
        }
        else if (joinsNext)
        {
            _ranges[next] = new SectorRange(lba, _ranges[next].Count + 1);
        }
        else
        {
            _ranges.Insert(next, new SectorRange(lba, 1));
        }
    }

    private sealed class RangeStartComparer : IComparer<SectorRange>
    {
        public static readonly RangeStartComparer Instance = new();

        public int Compare(SectorRange x, SectorRange y) => x.Start.CompareTo(y.Start);
    }
}
