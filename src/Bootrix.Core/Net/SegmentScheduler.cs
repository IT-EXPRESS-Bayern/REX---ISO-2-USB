// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Net;

/// <summary>
/// A stretch of the file that one worker is fetching. <see cref="Next"/> and <see cref="End"/> are only
/// touched through the scheduler, which owns the lock.
/// </summary>
internal sealed class Segment
{
    public Segment(long start, long end)
    {
        Start = start;
        Next = start;
        Written = start;
        End = end;
    }

    public long Start { get; }

    /// <summary>First byte nobody has claimed for writing yet.</summary>
    public long Next { get; internal set; }

    /// <summary>Bytes before this offset are on disk.</summary>
    public long Written { get; internal set; }

    /// <summary>Exclusive end; shrinks when another worker takes over the tail.</summary>
    public long End { get; internal set; }

    public DownloadSource? Source { get; set; }
}

/// <summary>
/// Hands out the missing parts of the file. When nothing is left to hand out, an idle worker splits the
/// largest segment still in progress and takes its second half (work stealing), so slow connections
/// do not hold up the finish.
/// </summary>
internal sealed class SegmentScheduler
{
    private readonly object _gate = new();
    private readonly List<Segment> _pending = [];
    private readonly List<Segment> _active = [];
    private readonly RangeSet _done;
    private readonly long _minSplit;
    private readonly Func<long, long> _alignDown;

    public SegmentScheduler(long length, RangeSet done, int workers, long minSplit, Func<long, long>? alignDown = null)
    {
        Length = length;
        _done = done;
        _minSplit = Math.Max(1, minSplit);
        _alignDown = alignDown ?? (offset => offset);
        PlanInitialSegments(workers);
    }

    public long Length { get; }

    public bool IsComplete
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count == 0 && _active.Count == 0 && _done.Total == Length;
            }
        }
    }

    public bool HasPending
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count > 0;
            }
        }
    }

    /// <summary>Bytes still missing, whether or not someone is working on them.</summary>
    public long Missing
    {
        get
        {
            lock (_gate)
            {
                return Length - _done.Total - _active.Sum(s => s.Written - s.Start);
            }
        }
    }

    /// <summary>The next segment for an idle worker: queued work first, otherwise half of the biggest running one.</summary>
    public Segment? TryTake()
    {
        lock (_gate)
        {
            if (_pending.Count > 0)
            {
                var segment = _pending[0];
                _pending.RemoveAt(0);
                _active.Add(segment);
                return segment;
            }

            Segment? victim = null;
            foreach (var candidate in _active)
            {
                if (candidate.End - candidate.Next >= 2 * _minSplit && (victim is null || candidate.End - candidate.Next > victim.End - victim.Next))
                {
                    victim = candidate;
                }
            }

            if (victim is null)
            {
                return null;
            }

            var middle = _alignDown(victim.Next + (victim.End - victim.Next) / 2);
            if (middle <= victim.Next || middle >= victim.End)
            {
                return null;
            }

            var stolen = new Segment(middle, victim.End);
            victim.End = middle;
            _active.Add(stolen);
            return stolen;
        }
    }

    /// <summary>
    /// Reserves up to <paramref name="wanted"/> bytes at the segment's current position. The reservation
    /// is made before the data is written, so a concurrent split can never cut into bytes in flight.
    /// </summary>
    public int Claim(Segment segment, int wanted, out long offset)
    {
        lock (_gate)
        {
            offset = segment.Next;
            var count = (int)Math.Min(wanted, segment.End - segment.Next);
            segment.Next += count;
            return count;
        }
    }

    public void Commit(Segment segment, int count)
    {
        lock (_gate)
        {
            segment.Written += count;
        }
    }

    /// <summary>The bytes the segment may still receive; zero once it is finished or was cut down to what it has.</summary>
    public (long Next, long End) Position(Segment segment)
    {
        lock (_gate)
        {
            return (segment.Next, segment.End);
        }
    }

    public ByteRange Complete(Segment segment)
    {
        lock (_gate)
        {
            _active.Remove(segment);
            _done.Add(segment.Start, segment.End);
            return new ByteRange(segment.Start, segment.End);
        }
    }

    /// <summary>Gives up a segment halfway: what is written counts as done, the rest goes back to the queue.</summary>
    public void Release(Segment segment)
    {
        lock (_gate)
        {
            _active.Remove(segment);
            _done.Add(segment.Start, segment.Written);
            if (segment.Written < segment.End)
            {
                _pending.Add(new Segment(segment.Written, segment.End));
            }
        }
    }

    /// <summary>Marks a range as missing again, for example because its digest did not match.</summary>
    public void Reopen(ByteRange range)
    {
        lock (_gate)
        {
            _done.Remove(range.Start, range.End);
            _pending.Add(new Segment(range.Start, range.End));
        }
    }

    public bool IsCovered(ByteRange range)
    {
        lock (_gate)
        {
            return _done.Covers(range.Start, range.End);
        }
    }

    /// <summary>Everything that is safely on disk, including the written part of running segments.</summary>
    public RangeSet Snapshot()
    {
        lock (_gate)
        {
            var snapshot = _done.Clone();
            foreach (var segment in _active)
            {
                snapshot.Add(segment.Start, segment.Written);
            }

            return snapshot;
        }
    }

    public int ActiveCount
    {
        get
        {
            lock (_gate)
            {
                return _active.Count;
            }
        }
    }

    private void PlanInitialSegments(int workers)
    {
        var gaps = _done.Gaps(Length).ToList();
        var missing = gaps.Sum(g => g.Length);
        if (missing == 0)
        {
            return;
        }

        var target = Math.Max(_minSplit, (missing + workers - 1) / workers);
        foreach (var gap in gaps)
        {
            var parts = (int)Math.Max(1, gap.Length / target);
            var start = gap.Start;
            for (var i = 1; i <= parts; i++)
            {
                var end = i == parts ? gap.End : _alignDown(gap.Start + gap.Length * i / parts);
                if (end <= start)
                {
                    continue;
                }

                _pending.Add(new Segment(start, end));
                start = end;
            }
        }
    }
}
