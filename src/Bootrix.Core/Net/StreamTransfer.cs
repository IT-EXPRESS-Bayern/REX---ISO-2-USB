// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers;
using System.Net;
using Bootrix.Core.Errors;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Bootrix.Core.Net;

/// <summary>
/// Fallback for servers that ignore range requests or do not announce a length: one plain GET, written from
/// the start. There is nothing to resume, so a broken connection starts over.
/// </summary>
internal sealed class StreamTransfer(
    HttpClient http,
    DownloadRequest request,
    SafeFileHandle file,
    TransferCounter counter,
    LinkRenewer renewer,
    TimeProvider time,
    ILogger log)
{
    private readonly DownloadOptions _options = request.Options;

    /// <summary>Downloads into the file and returns the number of bytes. <paramref name="initial"/> is the probe's open response, if any.</summary>
    public async Task<long> RunAsync(DownloadSource source, HttpResponseMessage? initial, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(_options.BufferSize);
        var response = initial;
        var failures = 0;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var link = source.Link;
                try
                {
                    try
                    {
                        response ??= await OpenAsync(link, cancellationToken).ConfigureAwait(false);
                        counter.Enter();
                        try
                        {
                            return await CopyAsync(response, buffer, cancellationToken).ConfigureAwait(false);
                        }
                        finally
                        {
                            counter.Leave();
                        }
                    }
                    catch (LinkExpiredException)
                    {
                        // A failure while renewing is handled like any other transient failure below.
                        response?.Dispose();
                        response = null;
                        var renewed = await renewer.RenewAsync(source, link.Generation, cancellationToken).ConfigureAwait(false);
                        response = renewed?.Body;
                    }
                }
                catch (TransientDownloadException ex)
                {
                    response?.Dispose();
                    response = null;

                    if (++failures > _options.MaxRetries)
                    {
                        throw new BootrixException(ErrorCode.DownloadFailed, ex.Message, ex) { Arguments = [ex.Message] };
                    }

                    log.LogDebug(ex, "Stream download failed ({Failures}/{Max}), starting over", failures, _options.MaxRetries);
                    counter.Discard(counter.Bytes);
                    RandomAccess.SetLength(file, 0);
                    await Task.Delay(DownloadHttp.BackoffDelay(failures, _options, ex.RetryAfter), time, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            response?.Dispose();
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<HttpResponseMessage> OpenAsync(SourceLink link, CancellationToken cancellationToken)
    {
        using var message = DownloadHttp.CreateRequest(link.Resolved, link.Origin, _options);
        var response = await DownloadHttp.SendAsync(http, message, _options, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.OK)
        {
            return response;
        }

        var failure = DownloadHttp.ForStatus(response);
        response.Dispose();
        throw failure;
    }

    private async Task<long> CopyAsync(HttpResponseMessage response, byte[] buffer, CancellationToken cancellationToken)
    {
        var announced = response.Content.Headers.ContentLength;

        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

        long position = 0;
        while (true)
        {
            stall.CancelAfter(_options.StallTimeout);
            int read;
            try
            {
                read = await body.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
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
                break;
            }

            try
            {
                await RandomAccess.WriteAsync(file, buffer.AsMemory(0, read), position, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex)
            {
                throw new BootrixException(ErrorCode.DownloadFailed, ex.Message, ex) { Arguments = [ex.Message] };
            }

            position += read;
            counter.Written(read);
        }

        if (announced is { } expected && position != expected)
        {
            throw new TransientDownloadException($"received {position} of {expected} bytes");
        }

        RandomAccess.SetLength(file, position);
        return position;
    }
}
