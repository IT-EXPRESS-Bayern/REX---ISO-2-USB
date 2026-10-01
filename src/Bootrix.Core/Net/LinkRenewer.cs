// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Microsoft.Extensions.Logging;

namespace Bootrix.Core.Net;

/// <summary>
/// Gets a source working again after the server turned it down with 403/410. The primary source asks the
/// request's resolver for a fresh address; the others run their redirect chain again, because CDNs
/// often put a signed, short-lived address at the end of it.
/// </summary>
internal sealed class LinkRenewer(HttpClient http, DownloadRequest request, long? length, ILogger log)
{
    /// <summary>
    /// Returns the new probe, or null if another worker renewed the same generation while we were waiting.
    /// The caller disposes the result.
    /// </summary>
    public async Task<ProbeResult?> RenewAsync(DownloadSource source, int seenGeneration, CancellationToken cancellationToken)
    {
        var options = request.Options;

        await source.RefreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (source.Link.Generation != seenGeneration)
            {
                return null;
            }

            if (++source.LinkRefreshes > options.MaxLinkRefreshes)
            {
                throw new BootrixException(ErrorCode.DownloadFailed, $"{source.Link.Origin} is still refused after {options.MaxLinkRefreshes} renewals")
                {
                    Arguments = ["HTTP 403"],
                };
            }

            var origin = source.Index == 0 && request.LinkResolver is not null
                ? await DownloadHttp.ResolveLinkAsync(request, cancellationToken).ConfigureAwait(false)
                : source.Link.Origin;

            log.LogInformation("Renewing link for source {Index} (attempt {Attempt})", source.Index, source.LinkRefreshes);

            ProbeResult probe;
            try
            {
                probe = await RemoteProbe.ResolveAsync(http, origin, options, cancellationToken).ConfigureAwait(false);
            }
            catch (LinkExpiredException ex)
            {
                throw new BootrixException(ErrorCode.DownloadFailed, $"{origin} is refused with {ex.Message} right after renewal")
                {
                    Arguments = [ex.Message],
                };
            }

            var previous = source.Link;
            if (length is { } expected && probe.Length != expected)
            {
                probe.Dispose();
                throw new ContentChangedException($"length changed from {expected} to {probe.Length}");
            }

            if (previous.ETag is not null && probe.ETag is not null && !string.Equals(previous.ETag, probe.ETag, StringComparison.Ordinal))
            {
                probe.Dispose();
                throw new ContentChangedException($"ETag changed from {previous.ETag} to {probe.ETag}");
            }

            source.Link = new SourceLink(origin, probe.Resolved, probe.ETag ?? previous.ETag, probe.LastModified ?? previous.LastModified, previous.Generation + 1);
            return probe;
        }
        finally
        {
            source.RefreshGate.Release();
        }
    }
}
