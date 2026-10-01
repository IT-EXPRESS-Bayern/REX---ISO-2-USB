// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using Bootrix.Core.Errors;

namespace Bootrix.Core.Net;

/// <param name="Resolved">Address that finally served the data, after all redirects.</param>
/// <param name="Length">Total size from <c>Content-Range</c> or <c>Content-Length</c>; null if the server did not say.</param>
/// <param name="RangeSupported">True only if the server answered the one-byte range request with 206.</param>
/// <param name="Body">The open response when the server ignored the range; it already is the full download.</param>
internal sealed record ProbeResult(Uri Resolved, long? Length, bool RangeSupported, string? ETag, DateTimeOffset? LastModified, HttpResponseMessage? Body)
    : IDisposable
{
    public void Dispose() => Body?.Dispose();
}

/// <summary>
/// Follows the redirect chain once and finds out whether the server really honours ranges. The
/// <c>Accept-Ranges</c> header is not trusted: proxies and CDNs advertise it and then answer 200.
/// </summary>
internal static class RemoteProbe
{
    public static async Task<ProbeResult> ResolveAsync(HttpClient http, Uri origin, DownloadOptions options, CancellationToken cancellationToken)
    {
        var current = origin;

        for (var hops = 0; ; hops++)
        {
            using var request = DownloadHttp.CreateRequest(current, origin, options, from: 0, to: 0);
            var response = await DownloadHttp.SendAsync(http, request, options, cancellationToken).ConfigureAwait(false);
            var keep = false;

            try
            {
                if (DownloadHttp.IsRedirect(response.StatusCode))
                {
                    current = NextHop(response, current, hops, options);
                    continue;
                }

                var result = response.StatusCode switch
                {
                    HttpStatusCode.PartialContent => FromPartialContent(response, current),
                    HttpStatusCode.OK => FromFullResponse(response, current),
                    HttpStatusCode.RequestedRangeNotSatisfiable => FromUnsatisfiableRange(response, current),
                    _ => throw DownloadHttp.ForStatus(response),
                };

                keep = result.Body is not null;
                return result;
            }
            finally
            {
                if (!keep)
                {
                    response.Dispose();
                }
            }
        }
    }

    private static Uri NextHop(HttpResponseMessage response, Uri current, int hops, DownloadOptions options)
    {
        if (hops >= options.MaxRedirects)
        {
            throw new BootrixException(ErrorCode.DownloadFailed, $"more than {options.MaxRedirects} redirects") { Arguments = ["too many redirects"] };
        }

        var location = response.Headers.Location
            ?? throw new BootrixException(ErrorCode.DownloadFailed, $"redirect without Location from {current}") { Arguments = ["invalid redirect"] };
        var next = location.IsAbsoluteUri ? location : new Uri(current, location);

        if (next.Scheme != Uri.UriSchemeHttps && next.Scheme != Uri.UriSchemeHttp)
        {
            throw new BootrixException(ErrorCode.DownloadFailed, $"redirect to {next.Scheme}") { Arguments = ["invalid redirect"] };
        }

        // A downgrade would let anyone on the path swap the file; integrity must not depend on the redirect target.
        if (current.Scheme == Uri.UriSchemeHttps && next.Scheme == Uri.UriSchemeHttp)
        {
            throw new BootrixException(ErrorCode.DownloadFailed, $"redirect from {current} to unencrypted {next}") { Arguments = ["redirect to http"] };
        }

        return next;
    }

    private static ProbeResult FromPartialContent(HttpResponseMessage response, Uri resolved)
    {
        var range = response.Content.Headers.ContentRange;
        if (range is not { HasRange: true, From: 0 } || !string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase))
        {
            return Unusable(response, resolved);
        }

        // "bytes 0-0/*" means the total is unknown; ranges would work but we could not plan segments.
        return new ProbeResult(resolved, range.Length, range.Length is not null, DownloadHttp.FormatETag(response), response.Content.Headers.LastModified, null);
    }

    private static ProbeResult FromFullResponse(HttpResponseMessage response, Uri resolved) =>
        new(resolved, response.Content.Headers.ContentLength, false, DownloadHttp.FormatETag(response), response.Content.Headers.LastModified, response);

    private static ProbeResult FromUnsatisfiableRange(HttpResponseMessage response, Uri resolved)
    {
        // "bytes */0": the resource is empty, so there is no byte 0 to ask for.
        if (response.Content.Headers.ContentRange is { Length: 0 })
        {
            return new ProbeResult(resolved, 0, false, DownloadHttp.FormatETag(response), response.Content.Headers.LastModified, null);
        }

        throw DownloadHttp.ForStatus(response);
    }

    private static ProbeResult Unusable(HttpResponseMessage response, Uri resolved) =>
        new(resolved, null, false, DownloadHttp.FormatETag(response), response.Content.Headers.LastModified, null);
}
