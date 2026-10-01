// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Net.Http.Headers;
using Bootrix.Core.Errors;

namespace Bootrix.Core.Tests.Catalog.Distros.Support;

/// <summary>What the live tests share: one client and the question how large the file behind an address is.</summary>
internal static class LiveVendor
{
    public static HttpClient Http { get; } = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>No route to the vendor from here (offline, blocked); an answer from the vendor, even an error, is not this.</summary>
    public static bool IsOffline(Exception error) =>
        error is BootrixException { Code: ErrorCode.CatalogUnavailable, InnerException: HttpRequestException { StatusCode: null } };

    /// <summary>A one-byte range request works where HEAD is refused, and Content-Range carries the full size.</summary>
    public static async Task<long> ProbeLength(Uri url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.True(response.StatusCode is HttpStatusCode.PartialContent or HttpStatusCode.OK, $"{url}: {(int)response.StatusCode}");

        return response.Content.Headers.ContentRange?.Length ?? response.Content.Headers.ContentLength ?? 0;
    }
}
