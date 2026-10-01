// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers;
using System.Net;
using Bootrix.Core.Errors;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Bootrix.Core.Net;

/// <summary>
/// One connection's worth of work: takes a segment, fetches it with a range request and writes it at its
/// offset. Transient failures are retried with back-off and continue where the data stopped.
/// </summary>
internal sealed class SegmentWorker(
    HttpClient http,
    DownloadRequest request,
    SourcePool sources,
    SegmentScheduler scheduler,
    SafeFileHandle file,
    PieceKeeper? pieces,
    TransferCounter counter,
    LinkRenewer renewer,
    long length,
    TimeProvider time,
    ILogger log)
{
    // A retry that moved at least this much data does not count against the segment's failure budget.
    private const int ProgressResetBytes = 64 * 1024;

    private readonly DownloadOptions _options = request.Options;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(_options.BufferSize);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var source = sources.Lease();
                if (source is null)
                {
                    return;
                }

                try
                {
                    var segment = scheduler.TryTake();
                    if (segment is null)
                    {
                        return;
                    }

                    segment.Source = source;
                    counter.Enter();
                    try
                    {
                        if (await DownloadSegmentAsync(segment, source, buffer, cancellationToken).ConfigureAwait(false))
                        {
                            var range = scheduler.Complete(segment);
                            source.LinkRefreshes = 0;
                            if (pieces is not null)
                            {
                                await pieces.CheckCompletedAsync(range, source, cancellationToken).ConfigureAwait(false);
                            }
                        }
                    }
                    finally
                    {
                        counter.Leave();
                    }
                }
                finally
                {
                    sources.Return(source);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Returns false if the segment was handed back because its source is being dropped.</summary>
    private async Task<bool> DownloadSegmentAsync(Segment segment, DownloadSource source, byte[] buffer, CancellationToken cancellationToken)
    {
        var failures = 0;
        var positionAtLastFailure = scheduler.Position(segment).Next;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (next, end) = scheduler.Position(segment);
            if (next >= end)
            {
                return true;
            }

            var link = source.Link;
            try
            {
                await FetchAsync(segment, link, buffer, cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (LinkExpiredException ex)
            {
                log.LogDebug("Source {Index} answered {Status}; renewing the link", source.Index, ex.Message);
                using var renewed = await renewer.RenewAsync(source, link.Generation, cancellationToken).ConfigureAwait(false);
                if (renewed is { RangeSupported: false })
                {
                    throw new ContentChangedException("the server stopped honouring range requests");
                }
            }
            catch (TransientDownloadException ex)
            {
                var position = scheduler.Position(segment).Next;
                failures = position - positionAtLastFailure >= ProgressResetBytes ? 1 : failures + 1;
                positionAtLastFailure = position;

                log.LogDebug(ex, "Segment {Start} from source {Index} failed ({Failures}/{Max})", segment.Start, source.Index, failures, _options.MaxRetries);

                if (failures > _options.MaxRetries)
                {
                    if (sources.Ban(source))
                    {
                        log.LogWarning("Dropping source {Url} after repeated failures", source.Spec.Url);
                        scheduler.Release(segment);
                        return false;
                    }

                    throw new BootrixException(ErrorCode.DownloadFailed, ex.Message, ex) { Arguments = [ex.Message] };
                }

                await Task.Delay(DownloadHttp.BackoffDelay(failures, _options, ex.RetryAfter), time, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task FetchAsync(Segment segment, SourceLink link, byte[] buffer, CancellationToken cancellationToken)
    {
        var (from, end) = scheduler.Position(segment);

        using var message = DownloadHttp.CreateRequest(link.Resolved, link.Origin, _options, from, end - 1, link);
        using var response = await DownloadHttp.SendAsync(http, message, _options, cancellationToken).ConfigureAwait(false);
        CheckResponse(response, from, link);

        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        while (true)
        {
            var (next, currentEnd) = scheduler.Position(segment);
            var wanted = (int)Math.Min(buffer.Length, currentEnd - next);
            if (wanted <= 0)
            {
                return;
            }

            stall.CancelAfter(_options.StallTimeout);
            int read;
            try
            {
                read = await body.ReadAsync(buffer.AsMemory(0, wanted), stall.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TransientDownloadException($"no data for {_options.StallTimeout.TotalSeconds:0} s");
            }
            catch (IOException ex)
            {
                throw new TransientDownloadException(ex.Message, null, ex);
            }

            if (read == 0)
            {
                throw new TransientDownloadException("connection closed before the range was complete");
            }

            // The range is reserved before the write; a worker splitting this segment meanwhile sees the new position.
            var claimed = scheduler.Claim(segment, read, out var offset);
            if (claimed == 0)
            {
                return;
            }

            await WriteAsync(buffer.AsMemory(0, claimed), offset, cancellationToken).ConfigureAwait(false);
            scheduler.Commit(segment, claimed);
            counter.Written(claimed);
        }
    }

    private void CheckResponse(HttpResponseMessage response, long from, SourceLink link)
    {
        switch (response.StatusCode)
        {
            case HttpStatusCode.PartialContent:
                var range = response.Content.Headers.ContentRange;
                if (range is not { HasRange: true } || range.From != from)
                {
                    throw new TransientDownloadException($"unexpected Content-Range '{range}' for a request at {from}");
                }

                if (range.Length is { } total && total != length)
                {
                    throw new ContentChangedException($"length changed from {length} to {total}");
                }

                break;

            // A server may answer 200 instead of 206 when the range starts at zero. That is only usable if it is
            // still the file we have been downloading; with a changed validator the other segments would not belong to it.
            // Anywhere else a 200 means ranges stopped working or the file changed.
            case HttpStatusCode.OK when from == 0 && response.Content.Headers.ContentLength == length && IsSameRepresentation(response, link):
                break;

            case HttpStatusCode.OK:
            case HttpStatusCode.RequestedRangeNotSatisfiable:
                throw new ContentChangedException($"server answered {(int)response.StatusCode} to a range request at {from}");

            default:
                throw DownloadHttp.ForStatus(response);
        }
    }

    private static bool IsSameRepresentation(HttpResponseMessage response, SourceLink link)
    {
        if (link.ETag is not null)
        {
            return string.Equals(DownloadHttp.FormatETag(response), link.ETag, StringComparison.Ordinal);
        }

        return link.LastModified is null || response.Content.Headers.LastModified == link.LastModified;
    }

    private async Task WriteAsync(ReadOnlyMemory<byte> data, long offset, CancellationToken cancellationToken)
    {
        try
        {
            await RandomAccess.WriteAsync(file, data, offset, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            // Disk full or the target went away; another connection will not fix that.
            throw new BootrixException(ErrorCode.DownloadFailed, ex.Message, ex) { Arguments = [ex.Message] };
        }
    }
}
