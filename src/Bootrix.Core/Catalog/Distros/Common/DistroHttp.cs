// SPDX-License-Identifier: GPL-3.0-or-later
using System.Net;
using System.Text;
using Bootrix.Core.Errors;

namespace Bootrix.Core.Catalog.Distros.Common;

/// <summary>
/// Fetches the small documents providers read (checksum files, release lists, directory pages) and keeps them for a
/// while, so listing variants and resolving one do not ask the vendor twice. Failures surface as
/// <see cref="ErrorCode.CatalogUnavailable"/>; callers never see an <see cref="HttpRequestException"/>.
/// </summary>
internal sealed class DistroHttp
{
    private const long MaxBytes = 16 * 1024 * 1024;
    private const int MaxEntries = 64;

    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(10);

    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);

    public DistroHttp(HttpClient http, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _time = time ?? TimeProvider.System;
    }

    public TimeProvider Time => _time;

    public async Task<byte[]> GetBytesAsync(Uri url, CancellationToken cancellationToken, TimeSpan? lifetime = null) =>
        (await FetchAsync(url, missingIsNull: false, lifetime, cancellationToken).ConfigureAwait(false))!;

    /// <summary>Like <see cref="GetBytesAsync"/>, but a 404 or 410 answer is "not there" instead of an error.</summary>
    public Task<byte[]?> TryGetBytesAsync(Uri url, CancellationToken cancellationToken, TimeSpan? lifetime = null) =>
        FetchAsync(url, missingIsNull: true, lifetime, cancellationToken);

    public async Task<string> GetStringAsync(Uri url, CancellationToken cancellationToken, TimeSpan? lifetime = null) =>
        Decode(await GetBytesAsync(url, cancellationToken, lifetime).ConfigureAwait(false));

    public async Task<string?> TryGetStringAsync(Uri url, CancellationToken cancellationToken, TimeSpan? lifetime = null)
    {
        var bytes = await TryGetBytesAsync(url, cancellationToken, lifetime).ConfigureAwait(false);
        return bytes is null ? null : Decode(bytes);
    }

    private static string Decode(byte[] bytes) => Encoding.UTF8.GetString(bytes).TrimStart('﻿');

    private async Task<byte[]?> FetchAsync(Uri url, bool missingIsNull, TimeSpan? lifetime, CancellationToken cancellationToken)
    {
        var key = url.AbsoluteUri;

        // A zero lifetime asks for a fresh copy, so an older cached one must not answer it either.
        if (lifetime is not { } fresh || fresh > TimeSpan.Zero)
        {
            lock (_gate)
            {
                if (_cache.TryGetValue(key, out var hit) && hit.Expires > _time.GetUtcNow())
                {
                    return hit.Body;
                }
            }
        }

        byte[] body;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.TryParseAdd($"{AppInfo.Name}/{AppInfo.Version}");

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (missingIsNull && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                return null;
            }

            response.EnsureSuccessStatusCode();
            await response.Content.LoadIntoBufferAsync(MaxBytes, cancellationToken).ConfigureAwait(false);
            body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw Unavailable(url, ex.StatusCode is { } status ? $"HTTP {(int)status}" : ex.Message, ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw Unavailable(url, "timed out", ex);
        }

        if (lifetime is { } keep && keep <= TimeSpan.Zero)
        {
            return body;
        }

        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (_cache.Count >= MaxEntries)
            {
                foreach (var stale in _cache.Where(e => e.Value.Expires <= now).Select(e => e.Key).ToList())
                {
                    _cache.Remove(stale);
                }

                if (_cache.Count >= MaxEntries)
                {
                    _cache.Clear();
                }
            }

            _cache[key] = new CacheEntry(body, now + (lifetime ?? DefaultLifetime));
        }

        return body;
    }

    private static BootrixException Unavailable(Uri url, string reason, Exception inner) =>
        new(ErrorCode.CatalogUnavailable, $"{url}: {reason}", inner);

    private readonly record struct CacheEntry(byte[] Body, DateTimeOffset Expires);
}
