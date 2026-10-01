// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Net;

/// <summary>Lock-free counters read by the progress reporter while the workers update them.</summary>
internal sealed class TransferCounter
{
    private long _bytes;
    private long _totalWritten;
    private int _active;

    /// <summary>Bytes of the file that are on disk and still count; goes down when a piece is thrown away.</summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>Everything ever written, never decreasing; a round that adds nothing to it made no progress.</summary>
    public long TotalWritten => Interlocked.Read(ref _totalWritten);

    public int Active => Volatile.Read(ref _active);

    public void Written(int count)
    {
        Interlocked.Add(ref _bytes, count);
        Interlocked.Add(ref _totalWritten, count);
    }

    public void Discard(long count) => Interlocked.Add(ref _bytes, -count);

    public void Seed(long count) => Interlocked.Exchange(ref _bytes, count);

    public void Enter() => Interlocked.Increment(ref _active);

    public void Leave() => Interlocked.Decrement(ref _active);
}
