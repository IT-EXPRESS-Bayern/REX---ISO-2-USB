// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;

namespace Bootrix.Core.Catalog;

/// <summary>HTTP clients for talking to vendor sites. They identify themselves honestly and keep no state between providers.</summary>
public static class CatalogHttp
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    /// <summary>For distribution mirrors and metadata: follows redirects, compresses, no cookies.</summary>
    public static HttpClient Create() => Create(useCookies: false);

    /// <summary>
    /// For Microsoft's download pages: their anti-bot handshake hands out cookies that the following
    /// requests have to return, so this client keeps its own cookie jar and shares it with nobody else.
    /// </summary>
    public static HttpClient CreateWithCookies() => Create(useCookies: true);

    private static HttpClient Create(bool useCookies)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = useCookies,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(15),
        };

        var client = new HttpClient(handler, disposeHandler: true) { Timeout = RequestTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppInfo.Name}/{AppInfo.Version}");
        return client;
    }
}
