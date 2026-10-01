// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Optical;

/// <summary>
/// Joins the progress of several drives that burn at the same time into one figure. The drives
/// report from their own threads, so every access is locked.
/// </summary>
public sealed class MultiDriveProgress(int driveCount)
{
    private readonly object _gate = new();
    private readonly BurnProgress?[] _latest = new BurnProgress?[driveCount];

    public void Update(int drive, BurnProgress progress)
    {
        lock (_gate)
        {
            _latest[drive] = progress;
        }
    }

    /// <summary>Mean over all drives; a drive that has not reported yet counts as 0, so the bar never jumps backwards when another one joins in.</summary>
    public double Fraction
    {
        get
        {
            lock (_gate)
            {
                return driveCount == 0 ? 0 : _latest.Sum(p => p?.Fraction ?? 0) / driveCount;
            }
        }
    }

    public long SectorsWritten
    {
        get
        {
            lock (_gate)
            {
                return _latest.Sum(p => p?.SectorsWritten ?? 0);
            }
        }
    }

    public long SectorsTotal
    {
        get
        {
            lock (_gate)
            {
                return _latest.Sum(p => p?.SectorCount ?? 0);
            }
        }
    }

    /// <summary>The phase of the drive that is furthest behind, because that is what everyone is waiting for.</summary>
    public BurnPhase? Phase
    {
        get
        {
            lock (_gate)
            {
                var reported = _latest.Where(p => p is not null).Select(p => p!.Value.Phase).ToList();
                return reported.Count == 0 ? null : reported.Min();
            }
        }
    }

    /// <summary>The longest remaining time of any drive, null while one of them has no estimate.</summary>
    public TimeSpan? Remaining
    {
        get
        {
            lock (_gate)
            {
                var estimates = _latest.Select(p => p?.Remaining).ToList();
                return estimates.Count == 0 || estimates.Any(e => e is null) ? null : estimates.Max();
            }
        }
    }
}
