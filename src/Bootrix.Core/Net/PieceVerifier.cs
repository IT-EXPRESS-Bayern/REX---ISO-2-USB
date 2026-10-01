// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Win32.SafeHandles;

namespace Bootrix.Core.Net;

/// <summary>
/// Checks slices of the file against their digests as soon as they are complete, so that a corrupt slice is
/// fetched again right away instead of surfacing as a hash mismatch of the whole file at the end.
/// </summary>
internal sealed class PieceVerifier
{
    private const int Open = 0;
    private const int Checking = 1;
    private const int Verified = 2;

    private readonly IReadOnlyList<PieceHash> _pieces;
    private readonly SafeFileHandle _file;
    private readonly int[] _state;
    private readonly int[] _failures;
    private readonly long[] _starts;

    public PieceVerifier(IReadOnlyList<PieceHash> pieces, SafeFileHandle file)
    {
        _pieces = pieces;
        _file = file;
        _state = new int[pieces.Count];
        _failures = new int[pieces.Count];
        _starts = [.. pieces.Select(p => p.Offset)];
    }

    public int Count => _pieces.Count;

    /// <summary>Start of the piece containing <paramref name="offset"/>; segments are split on these boundaries.</summary>
    public long AlignDown(long offset) => _starts[IndexOf(offset)];

    public ByteRange RangeOf(int index) => new(_pieces[index].Offset, _pieces[index].End);

    /// <summary>How often a piece has failed so far, counting this call.</summary>
    public int RecordFailure(int index) => Interlocked.Increment(ref _failures[index]);

    /// <summary>Verifies the pieces touched by a finished range that are now complete. Returns the pieces that failed.</summary>
    public Task<IReadOnlyList<int>> VerifyCompletedAsync(ByteRange completed, Func<ByteRange, bool> isCovered, CancellationToken cancellationToken)
    {
        var first = IndexOf(completed.Start);
        var last = IndexOf(completed.End - 1);
        return VerifyAsync(Enumerable.Range(first, last - first + 1), isCovered, cancellationToken);
    }

    public Task<IReadOnlyList<int>> VerifyAllCoveredAsync(Func<ByteRange, bool> isCovered, CancellationToken cancellationToken) =>
        VerifyAsync(Enumerable.Range(0, _pieces.Count), isCovered, cancellationToken);

    private async Task<IReadOnlyList<int>> VerifyAsync(IEnumerable<int> candidates, Func<ByteRange, bool> isCovered, CancellationToken cancellationToken)
    {
        var failed = new List<int>();

        foreach (var index in candidates)
        {
            // Coverage is tested before claiming: a worker that claimed first and then found the piece
            // incomplete could make the worker that completes it skip the check.
            if (!isCovered(RangeOf(index)) || Interlocked.CompareExchange(ref _state[index], Checking, Open) != Open)
            {
                continue;
            }

            var piece = _pieces[index];
            var ok = await FileHasher.MatchesAsync(_file, piece.Offset, piece.Length, piece.Hash, cancellationToken).ConfigureAwait(false);
            Volatile.Write(ref _state[index], ok ? Verified : Open);

            if (!ok)
            {
                failed.Add(index);
            }
        }

        return failed;
    }

    private int IndexOf(long offset)
    {
        var index = Array.BinarySearch(_starts, offset);
        return index >= 0 ? index : Math.Max(0, ~index - 1);
    }
}
