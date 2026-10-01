// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Microsoft.Extensions.Logging;

namespace Bootrix.Core.Catalog.Rescue;

/// <summary>
/// Fetches the signed rescue catalog and hands it to <see cref="RescueCatalogStore"/>, which does all the checking.
/// The address is a parameter so that a company can publish its own signed catalog; only the keys the verifier trusts
/// decide whether a document is accepted, not where it was downloaded from.
/// </summary>
public sealed class RescueCatalogUpdater
{
    private const int MaxEnvelopeBytes = 4 * 1024 * 1024;

    private readonly RescueCatalogStore _store;
    private readonly HttpClient _http;
    private readonly Uri _manifestUrl;
    private readonly ILogger<RescueCatalogUpdater> _logger;

    public RescueCatalogUpdater(RescueCatalogStore store, HttpClient http, Uri manifestUrl, ILogger<RescueCatalogUpdater> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(manifestUrl);
        ArgumentNullException.ThrowIfNull(logger);

        if (!manifestUrl.IsAbsoluteUri || manifestUrl.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("The catalog address must be an absolute https address.", nameof(manifestUrl));
        }

        _store = store;
        _http = http;
        _manifestUrl = manifestUrl;
        _logger = logger;
    }

    public async Task<RescueCatalogUpdate> UpdateAsync(CancellationToken cancellationToken)
    {
        var envelope = await FetchAsync(cancellationToken).ConfigureAwait(false);

        RescueCatalogUpdate result;
        try
        {
            result = _store.Apply(envelope);
        }
        catch (IOException ex)
        {
            throw new BootrixException(ErrorCode.CatalogUnavailable, "the verified catalog could not be stored: " + ex.Message, ex);
        }

        _logger.LogInformation("Rescue catalog from {Url}: {Status}, version {Version}", _manifestUrl, result.Status, result.Version);
        return result;
    }

    private async Task<byte[]> FetchAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(_manifestUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength > MaxEnvelopeBytes)
            {
                throw new BootrixException(ErrorCode.CatalogUnavailable, "the catalog document is unreasonably large");
            }

            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await ReadLimitedAsync(body, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            throw new BootrixException(ErrorCode.CatalogUnavailable, $"{_manifestUrl}: {ex.Message}", ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // The HttpClient timeout, not the caller, ended the request.
            throw new BootrixException(ErrorCode.CatalogUnavailable, $"{_manifestUrl}: timed out", ex);
        }
    }

    /// <summary>Servers that omit Content-Length are held to the same limit, so a stream that never ends cannot fill memory.</summary>
    private static async Task<byte[]> ReadLimitedAsync(Stream body, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];

        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxEnvelopeBytes)
            {
                throw new BootrixException(ErrorCode.CatalogUnavailable, "the catalog document is unreasonably large");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
