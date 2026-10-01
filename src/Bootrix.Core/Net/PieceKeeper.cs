// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Microsoft.Extensions.Logging;

namespace Bootrix.Core.Net;

/// <summary>
/// Ties piece verification to the running download: a slice that fails its digest is thrown away and fetched
/// again. The source that delivered it is dropped when another one is left; otherwise the same source gets a
/// few more tries before the download fails.
/// </summary>
internal sealed class PieceKeeper(
    PieceVerifier verifier,
    SegmentScheduler scheduler,
    TransferCounter counter,
    SourcePool sources,
    ILogger log)
{
    private const int MaxFailuresPerPiece = 3;

    /// <summary>Verifies the pieces that a just finished range completed. <paramref name="source"/> is blamed for bad ones.</summary>
    public async Task CheckCompletedAsync(ByteRange completed, DownloadSource source, CancellationToken cancellationToken)
    {
        var failed = await verifier.VerifyCompletedAsync(completed, scheduler.IsCovered, cancellationToken).ConfigureAwait(false);
        foreach (var index in failed)
        {
            Reject(index, source);
        }
    }

    /// <summary>
    /// Verifies every complete piece that has not been checked yet, for example ones that straddle segment
    /// boundaries. Returns how many were rejected and are queued again.
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var failed = await verifier.VerifyAllCoveredAsync(scheduler.IsCovered, cancellationToken).ConfigureAwait(false);
        foreach (var index in failed)
        {
            Reject(index, null);
        }

        return failed.Count;
    }

    private void Reject(int index, DownloadSource? culprit)
    {
        var range = verifier.RangeOf(index);
        scheduler.Reopen(range);
        counter.Discard(range.Length);
        var failures = verifier.RecordFailure(index);

        log.LogWarning("Piece {Index} ({Start}-{End}) failed its digest, source {Url}", index, range.Start, range.End, culprit?.Spec.Url);

        if (culprit is not null && sources.Ban(culprit))
        {
            return;
        }

        if (failures > MaxFailuresPerPiece)
        {
            throw new BootrixException(ErrorCode.DownloadHashMismatch, $"piece {index} at offset {range.Start} keeps failing its digest");
        }
    }
}
