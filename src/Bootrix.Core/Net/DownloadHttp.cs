// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
using Bootrix.Core.Errors;

namespace Bootrix.Core.Net;

/// <summary>Request construction, status classification and back-off shared by the probe and the workers.</summary>
internal static class DownloadHttp
{
    // Credentials must not follow a redirect to another host.
    private static readonly string[] SensitiveHeaders = ["Authorization", "Cookie", "Proxy-Authorization"];

    public static HttpMessageHandler CreateDefaultHandler(DownloadOptions options) => new SocketsHttpHandler
    {
        // Redirects are resolved once by the probe; segments go straight to the final address.
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false,
        MaxConnectionsPerServer = options.MaxSegments,
        ConnectTimeout = options.ConnectTimeout,
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
    };

    public static HttpRequestMessage CreateRequest(Uri uri, Uri origin, DownloadOptions options, long? from = null, long? to = null, SourceLink? validators = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri)
        {
            // One TCP connection per concurrent request; HTTP/2 would multiplex them and defeat the point.
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

        request.Headers.TryAddWithoutValidation("User-Agent", options.UserAgent);
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");

        var sameHost = string.Equals(uri.Host, origin.Host, StringComparison.OrdinalIgnoreCase);
        foreach (var (name, value) in options.Headers)
        {
            if (sameHost || !SensitiveHeaders.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        if (from is not null)
        {
            request.Headers.Range = new RangeHeaderValue(from, to);

            // A validator makes the server answer 200 with the whole file instead of splicing bytes of two versions.
            if (validators?.ETag is { } etag && !etag.StartsWith("W/", StringComparison.Ordinal) && EntityTagHeaderValue.TryParse(etag, out var tag))
            {
                request.Headers.IfRange = new RangeConditionHeaderValue(tag);
            }
            else if (validators?.LastModified is { } modified)
            {
                request.Headers.IfRange = new RangeConditionHeaderValue(modified);
            }
        }

        return request;
    }

    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, DownloadOptions options, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.ResponseTimeout);

        try
        {
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TransientDownloadException($"no response within {options.ResponseTimeout.TotalSeconds:0} s");
        }
        catch (HttpRequestException ex) when (IsPermanent(ex))
        {
            throw new BootrixException(ErrorCode.DownloadFailed, ex.Message, ex) { Arguments = [ex.Message] };
        }
        catch (HttpRequestException ex)
        {
            throw new TransientDownloadException(ex.Message, null, ex);
        }
    }

    public static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    /// <summary>Throws the exception matching a response that is not usable as data.</summary>
    public static Exception ForStatus(HttpResponseMessage response)
    {
        var status = response.StatusCode;
        var code = (int)status;

        if (status is HttpStatusCode.Forbidden or HttpStatusCode.Gone || IsRedirect(status))
        {
            return new LinkExpiredException(status);
        }

        if (status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
        {
            return new TransientDownloadException($"HTTP {code}", RetryAfter(response));
        }

        if (status == HttpStatusCode.RequestTimeout || code >= 500)
        {
            return new TransientDownloadException($"HTTP {code}");
        }

        var detail = $"HTTP {code} for {response.RequestMessage?.RequestUri}";
        return new BootrixException(ErrorCode.DownloadFailed, detail) { Arguments = [$"HTTP {code}"] };
    }

    public static TimeSpan BackoffDelay(int failures, DownloadOptions options, TimeSpan? retryAfter)
    {
        var exponent = Math.Min(Math.Max(failures - 1, 0), 20);
        var ticks = Math.Min(options.RetryBaseDelay.Ticks * (1L << exponent), options.RetryMaxDelay.Ticks);

        // Jitter keeps parallel segments from retrying in lockstep.
        var delay = TimeSpan.FromTicks((long)(ticks * (0.5 + Random.Shared.NextDouble() / 2)));

        if (retryAfter is { } wait)
        {
            var capped = wait > options.MaxRetryAfter ? options.MaxRetryAfter : wait;
            return capped > delay ? capped : delay;
        }

        return delay;
    }

    /// <summary>Asks the request's resolver for a new address and rejects anything that is not an http(s) URL.</summary>
    public static async Task<Uri> ResolveLinkAsync(DownloadRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var uri = await request.LinkResolver!(cancellationToken).ConfigureAwait(false);
            return uri is { IsAbsoluteUri: true } && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                ? uri
                : throw new BootrixException(ErrorCode.DownloadFailed, $"the link resolver returned '{uri}'") { Arguments = ["invalid address"] };
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or BootrixException))
        {
            throw new BootrixException(ErrorCode.DownloadFailed, $"the link resolver failed: {ex.Message}", ex) { Arguments = [ex.Message] };
        }
    }

    public static string? FormatETag(HttpResponseMessage response) => response.Headers.ETag?.ToString();

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        if (header?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        return null;
    }

    private static bool IsPermanent(HttpRequestException ex) =>
        ex.HttpRequestError is HttpRequestError.SecureConnectionError
            or HttpRequestError.UserAuthenticationError
            or HttpRequestError.VersionNegotiationError;
}
