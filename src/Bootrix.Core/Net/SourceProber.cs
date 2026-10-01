// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Microsoft.Extensions.Logging;

namespace Bootrix.Core.Net;

internal sealed record ProbedSource(int Index, Uri Origin, ProbeResult? Probe, Exception? Error);

/// <summary>Probes all sources of a request in parallel; each one retries transient failures and the primary renews an expired link.</summary>
internal sealed class SourceProber(HttpClient http, DownloadRequest request, TimeProvider time, ILogger log)
{
    private readonly DownloadOptions _options = request.Options;

    /// <summary>
    /// Returns the sources that answered. If none did, the primary's error is thrown; a failing mirror alone
    /// is only logged, since the others can still serve the file.
    /// </summary>
    public async Task<IReadOnlyList<ProbedSource>> ProbeAllAsync(CancellationToken cancellationToken)
    {
        var tasks = request.Sources.Select((_, index) => ProbeSourceAsync(index, cancellationToken)).ToList();

        ProbedSource[] attempts;
        try
        {
            attempts = await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            // A probe that finished before another one was cancelled may hold an open response.
            foreach (var task in tasks.Where(t => t.IsCompletedSuccessfully))
            {
                task.Result.Probe?.Dispose();
            }

            throw;
        }

        var answered = attempts.Where(a => a.Probe is not null).ToList();
        foreach (var failed in attempts.Where(a => a.Probe is null))
        {
            log.LogWarning(failed.Error, "Source {Url} is not usable", request.Sources[failed.Index].Url);
        }

        if (answered.Count > 0)
        {
            return answered;
        }

        var error = attempts[0].Error!;
        throw error as BootrixException
            ?? new BootrixException(ErrorCode.DownloadFailed, error.Message, error) { Arguments = [error.Message] };
    }

    private async Task<ProbedSource> ProbeSourceAsync(int index, CancellationToken cancellationToken)
    {
        var origin = request.Sources[index].Url;
        var failures = 0;
        var renewals = 0;

        while (true)
        {
            try
            {
                var probe = await RemoteProbe.ResolveAsync(http, origin, _options, cancellationToken).ConfigureAwait(false);
                return new ProbedSource(index, origin, probe, null);
            }
            catch (TransientDownloadException ex)
            {
                if (++failures > _options.MaxRetries)
                {
                    return new ProbedSource(index, origin, null, new BootrixException(ErrorCode.DownloadFailed, ex.Message, ex) { Arguments = [ex.Message] });
                }

                await Task.Delay(DownloadHttp.BackoffDelay(failures, _options, ex.RetryAfter), time, cancellationToken).ConfigureAwait(false);
            }
            catch (LinkExpiredException ex) when (index == 0 && request.LinkResolver is not null && ++renewals <= _options.MaxLinkRefreshes)
            {
                log.LogInformation("{Url} answered {Status}; asking the resolver for a new address", origin, ex.Message);
                origin = await DownloadHttp.ResolveLinkAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (LinkExpiredException ex)
            {
                return new ProbedSource(index, origin, null, new BootrixException(ErrorCode.DownloadFailed, $"{origin} answered {ex.Message}") { Arguments = [ex.Message] });
            }
            catch (BootrixException ex)
            {
                return new ProbedSource(index, origin, null, ex);
            }
        }
    }
}
