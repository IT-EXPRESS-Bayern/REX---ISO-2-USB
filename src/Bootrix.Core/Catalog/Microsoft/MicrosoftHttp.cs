// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using Bootrix.Core.Errors;

namespace Bootrix.Core.Catalog.Microsoft;

internal sealed record MicrosoftResponse(HttpStatusCode Status, byte[] Body, TimeSpan? RetryAfter, Uri Url);

/// <summary>
/// Requests to Microsoft's sites. The client identifies itself as what it is; retrying is left to the caller, and
/// every transport failure surfaces as <see cref="ErrorCode.CatalogUnavailable"/> with the technical reason in the
/// detail. Redirects are followed here so that the result does not depend on how the injected client is configured.
/// </summary>
internal static class MicrosoftHttp
{
    private const int MaxRedirects = 5;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Fetches a document and returns it with its status, whatever that is; only the transport can fail.</summary>
    public static async Task<MicrosoftResponse> GetAsync(HttpClient http, Uri url, Uri? referer, int maxBytes, CancellationToken cancellationToken)
    {
        var current = url;

        for (var hops = 0; ; hops++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.TryAddWithoutValidation("User-Agent", $"{AppInfo.Name}/{AppInfo.Version}");
            if (referer is not null)
            {
                request.Headers.Referrer = referer;
            }

            var (response, location) = await SendOnceAsync(http, request, maxBytes, cancellationToken).ConfigureAwait(false);
            if (location is null)
            {
                return response with { Url = current };
            }

            if (hops >= MaxRedirects)
            {
                throw Unavailable($"{url} redirects more than {MaxRedirects} times");
            }

            var next = location.IsAbsoluteUri ? location : new Uri(current, location);
            if (next.Scheme != Uri.UriSchemeHttps)
            {
                throw Unavailable($"{current} redirects to {next}, which is not https");
            }

            current = next;
        }
    }

    public static BootrixException Unavailable(string detail, Exception? inner = null) =>
        new(ErrorCode.CatalogUnavailable, detail, inner);

    /// <summary>The status as a technical message, with the wait the server asks for if it sent one.</summary>
    public static BootrixException ForStatus(Uri url, HttpStatusCode status, TimeSpan? retryAfter)
    {
        var wait = retryAfter is { } delay ? $", retry after {delay.TotalSeconds:0} s" : string.Empty;
        return Unavailable($"{url} answered HTTP {(int)status}{wait}");
    }

    private static async Task<(MicrosoftResponse Response, Uri? Redirect)> SendOnceAsync(
        HttpClient http,
        HttpRequestMessage request,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var retryAfter = RetryAfter(response);

            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
                or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect
                && response.Headers.Location is { } location)
            {
                return (new MicrosoftResponse(response.StatusCode, [], retryAfter, request.RequestUri!), location);
            }

            if (response.Content.Headers.ContentLength > maxBytes)
            {
                throw Unavailable($"{request.RequestUri} announces {response.Content.Headers.ContentLength} bytes, more than the {maxBytes} accepted");
            }

            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await body.ReadAsync(chunk, timeout.Token).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    throw Unavailable($"{request.RequestUri} delivers more than the {maxBytes} bytes accepted");
                }

                buffer.Write(chunk, 0, read);
            }

            return (new MicrosoftResponse(response.StatusCode, buffer.ToArray(), retryAfter, request.RequestUri!), null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Unavailable($"{request.RequestUri} did not answer within {RequestTimeout.TotalSeconds:0} s");
        }
        catch (HttpRequestException ex)
        {
            throw Unavailable($"{request.RequestUri}: {ex.Message}", ex);
        }
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        return header?.Date is { } date ? date - DateTimeOffset.UtcNow : null;
    }
}
