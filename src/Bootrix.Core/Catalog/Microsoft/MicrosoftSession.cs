// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Bootrix.Core.Catalog.Microsoft;

/// <summary>Where a page's scripts talk to, and the page that the requests claim to come from.</summary>
internal sealed record MicrosoftEndpoints(Uri ApiBase, Uri FingerprintScript, Uri Referer, Uri ManualUrl)
{
    public static MicrosoftEndpoints For(MicrosoftPage page, DownloadPage content) =>
        new(content.ApiBase, content.FingerprintScript, page.PageUrl, page.ManualUrl);
}

/// <summary>
/// One visitor session at Microsoft's download service, as the page's own script runs it: a random session id, the
/// "ov-df" handshake that tells the service a page was loaded, and then API calls that carry the id. The id ties
/// the calls together; the service refuses link requests whose session never did the handshake.
/// </summary>
internal sealed partial class MicrosoftSession(HttpClient http, MicrosoftEndpoints endpoints, TimeProvider time, ILogger logger)
{
    private const int MaxBytes = 4 * 1024 * 1024;

    // Fixed values of Microsoft's page script: the profile of the download site and the organisation of the profiling tag.
    private const string Profile = "606624d44113";
    private const string TagOrganisation = "y6jn8c31";

    private bool _handshakeDone;

    public string Id { get; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Fetches the script that Microsoft's fingerprinting service hands out for the session and calls the address
    /// it names, exactly the first request the page's hidden frame makes. The frame's own script, which would
    /// collect browser details, is not run. With <paramref name="registerTag"/> the session is also announced at
    /// the profiling service, a step the pages needed in earlier years and that is only tried after a refusal.
    /// </summary>
    public async Task HandshakeAsync(bool registerTag, CancellationToken cancellationToken)
    {
        if (registerTag)
        {
            await GetAsync(new Uri($"https://vlscppe.microsoft.com/tags?org_id={TagOrganisation}&session_id={Id}"), cancellationToken).ConfigureAwait(false);
        }

        var script = await GetAsync(new Uri(endpoints.FingerprintScript.AbsoluteUri + Id), cancellationToken).ConfigureAwait(false);

        var address = FrameAddress().Match(script);
        var ticks = ServerTicks().Match(script);
        if (!address.Success || !ticks.Success || !Uri.TryCreate(address.Groups["url"].Value, UriKind.Absolute, out var frame) || frame.Scheme != Uri.UriSchemeHttps)
        {
            throw MicrosoftHttp.Unavailable("the script of the fingerprinting service no longer has the expected shape (frame address or rticks missing)");
        }

        // "mdt" is the client's clock in milliseconds, "rticks" the server's clock taken from the script.
        var ping = $"{frame.AbsoluteUri}&mdt={time.GetUtcNow().ToUnixTimeMilliseconds()}&rticks={ticks.Groups["ticks"].Value}";
        await GetAsync(new Uri(ping), cancellationToken).ConfigureAwait(false);

        _handshakeDone = true;
        logger.LogDebug("Handshake for session {Session} done", Id);
    }

    /// <summary>
    /// Lists the languages of one edition. The list needs no handshake today, so none is made unless the service
    /// says "rejected", in which case it is done once and the question repeated.
    /// </summary>
    public async Task<SkuResponse> GetSkusAsync(int editionId, CancellationToken cancellationToken)
    {
        var url = ApiUrl("getskuinformationbyproductedition", editionId.ToString(CultureInfo.InvariantCulture), "undefined");
        var skus = Parse(await GetAsync(url, cancellationToken).ConfigureAwait(false), MicrosoftApiResponses.ParseSkus);

        if (skus.Errors.Any(e => e.IsSentinelReject) && !_handshakeDone)
        {
            await HandshakeAsync(registerTag: false, cancellationToken).ConfigureAwait(false);
            skus = Parse(await GetAsync(url, cancellationToken).ConfigureAwait(false), MicrosoftApiResponses.ParseSkus);
        }

        return skus;
    }

    public async Task<LinkResponse> GetLinksAsync(string skuId, bool registerTag, CancellationToken cancellationToken)
    {
        if (!_handshakeDone)
        {
            await HandshakeAsync(registerTag, cancellationToken).ConfigureAwait(false);
        }

        var url = ApiUrl("GetProductDownloadLinksBySku", "undefined", Uri.EscapeDataString(skuId));
        return Parse(await GetAsync(url, cancellationToken).ConfigureAwait(false), MicrosoftApiResponses.ParseLinks);
    }

    /// <summary>
    /// The page's script fills unused parameters with the text "undefined" (a JavaScript value turned into
    /// a string); the service expects that spelling.
    /// </summary>
    private Uri ApiUrl(string operation, string editionId, string sku) =>
        new($"{endpoints.ApiBase.AbsoluteUri.TrimEnd('/')}/{operation}?profile={Profile}&ProductEditionId={editionId}&SKU={sku}&friendlyFileName=undefined&Locale=en-US&sessionID={Id}");

    private static T Parse<T>(string json, Func<string, T> parse)
    {
        try
        {
            return parse(json);
        }
        catch (InvalidDataException ex)
        {
            throw MicrosoftHttp.Unavailable($"Unexpected answer from the download service: {ex.Message}", ex);
        }
    }

    private async Task<string> GetAsync(Uri url, CancellationToken cancellationToken)
    {
        var response = await MicrosoftHttp.GetAsync(http, url, endpoints.Referer, MaxBytes, cancellationToken).ConfigureAwait(false);

        if (response.Status == HttpStatusCode.Forbidden)
        {
            throw new MicrosoftDownloadBlockedException("HTTP 403", endpoints.ManualUrl, $"{url.Host} answered HTTP 403");
        }

        return response.Status is >= HttpStatusCode.OK and < HttpStatusCode.Ambiguous
            ? Encoding.UTF8.GetString(response.Body)
            : throw MicrosoftHttp.ForStatus(url, response.Status, response.RetryAfter);
    }

    [GeneratedRegex(@"url\s*:\s*""(?<url>https://[^""]+)""")]
    private static partial Regex FrameAddress();

    [GeneratedRegex(@"rticks=""\s*\+\s*(?<ticks>\d+)")]
    private static partial Regex ServerTicks();
}
