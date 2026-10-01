// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Jobs;

/// <summary>
/// Exponentially smoothed throughput. A plain average over the whole transfer reacts far too
/// slowly when a USB stick drops out of its SLC cache, so the weight of a sample depends on
/// how much time it covers.
/// </summary>
public sealed class SpeedEstimator
{
    private readonly TimeProvider _time;
    private readonly double _halfLifeSeconds;
    private long _lastBytes;
    private long _lastTimestamp;
    private bool _started;

    public SpeedEstimator(TimeProvider? time = null, double halfLifeSeconds = 3.0)
    {
        _time = time ?? TimeProvider.System;
        _halfLifeSeconds = halfLifeSeconds;
    }

    public double BytesPerSecond { get; private set; }

    public void Update(long bytesDone)
    {
        var now = _time.GetTimestamp();
        if (!_started)
        {
            _started = true;
            _lastBytes = bytesDone;
            _lastTimestamp = now;
            return;
        }

        var seconds = _time.GetElapsedTime(_lastTimestamp, now).TotalSeconds;
        if (seconds < 0.05)
        {
            return;
        }

        var instant = (bytesDone - _lastBytes) / seconds;
        if (BytesPerSecond <= 0)
        {
            BytesPerSecond = instant;
        }
        else
        {
            var weight = 1 - Math.Pow(0.5, seconds / _halfLifeSeconds);
            BytesPerSecond += (instant - BytesPerSecond) * weight;
        }

        _lastBytes = bytesDone;
        _lastTimestamp = now;
    }

    public TimeSpan? Remaining(long bytesDone, long bytesTotal)
    {
        if (BytesPerSecond < 1 || bytesTotal <= 0 || bytesDone >= bytesTotal)
        {
            return null;
        }

        return TimeSpan.FromSeconds((bytesTotal - bytesDone) / BytesPerSecond);
    }

    public void Reset()
    {
        BytesPerSecond = 0;
        _started = false;
    }
}
