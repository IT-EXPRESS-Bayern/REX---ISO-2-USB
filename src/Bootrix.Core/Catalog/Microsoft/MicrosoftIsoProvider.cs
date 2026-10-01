// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net;
using System.Text;
using Bootrix.Core.Errors;
using Bootrix.Core.Net;
using Microsoft.Extensions.Logging;

namespace Bootrix.Core.Catalog.Microsoft;

/// <summary>
/// Windows ISO files from the software-download pages of microsoft.com, the way the pages' own scripts fetch them:
/// editions are read from the page, languages and addresses from the connector API behind it, after the
/// "ov-df" handshake that the page performs when it loads. The requests identify themselves as Bootrix and nothing
/// else is done to look like a browser; when Microsoft answers with its "715-123130" refusal the provider reports
/// <see cref="MicrosoftDownloadBlockedException"/> and the download page's address so that the user can fetch the
/// file by hand (or the caller can fall back to <see cref="MediaCreationToolProvider"/>).
/// </summary>
/// <remarks>
/// Addresses are valid for 24 hours and are bound to the session that asked, so every
/// <see cref="DownloadRequest"/> carries a <see cref="DownloadRequest.LinkResolver"/> that repeats the whole
/// exchange. The API gives no size, so <see cref="DownloadRequest.ExpectedSize"/> stays empty and the downloader
/// takes it from the server. The page prints a SHA-256 for every ISO in a table; it is passed on when exactly one
/// row fits the language and word size, and left out otherwise. (Checked on 2026-10-01 for the German 64-bit ISO
/// of Windows 11: the file's digest equals the table's value.)
/// </remarks>
public sealed class MicrosoftIsoProvider : ICatalogProvider
{
    public const string ProviderId = "microsoft";

    private const int MaxPageBytes = 4 * 1024 * 1024;

    private readonly HttpClient _http;
    private readonly ILogger<MicrosoftIsoProvider> _logger;
    private readonly TimeProvider _time;

    public MicrosoftIsoProvider(HttpClient http, ILogger<MicrosoftIsoProvider> logger, TimeProvider? timeProvider = null)
    {
        _http = http;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    public string Id => ProviderId;

    public Task<IReadOnlyList<CatalogProduct>> ListProductsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<CatalogProduct> products =
        [
            .. MicrosoftProducts.All.Select(p => new CatalogProduct
            {
                Id = p.Id,
                Provider = ProviderId,
                Family = CatalogFamily.Windows,
                Name = p.Name,
                Description = p.Description,
                Homepage = p.Pages[0].ManualUrl.AbsoluteUri,
                License = "proprietary",
            }),
        ];

        return Task.FromResult(products);
    }

    public async Task<IReadOnlyList<CatalogVariant>> ListVariantsAsync(string productId, CancellationToken cancellationToken)
    {
        var product = MicrosoftProducts.Find(productId)
            ?? throw new ArgumentException($"Unknown product '{productId}'.", nameof(productId));

        var offers = new List<Offer>();
        BootrixException? failure = null;

        // One page failing must not hide the others (Arm64 may be down while x64 works); only a total failure is an error.
        foreach (var page in product.Pages)
        {
            try
            {
                offers.AddRange(await ListPageAsync(page, cancellationToken).ConfigureAwait(false));
            }
            catch (BootrixException ex)
            {
                _logger.LogWarning(ex, "Download page {Page} could not be read", page.Path);
                failure ??= ex;
            }
        }

        return offers.Count > 0
            ? Merge(product, offers)
            : throw (failure ?? MicrosoftHttp.Unavailable($"{product.Name} lists no language"));
    }

    public async Task<DownloadRequest> ResolveAsync(CatalogVariant variant, string? architecture, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(variant);

        var arch = ArchitectureChoice.Choose(variant, architecture);
        var route = Route.From(variant, arch);

        var content = await FetchPageAsync(route.Page, cancellationToken).ConfigureAwait(false);
        var endpoints = MicrosoftEndpoints.For(route.Page, content);

        var (links, sku) = await RequestLinksAsync(route, endpoints, cancellationToken).ConfigureAwait(false);
        var link = Pick(links, arch, route.Page);

        var hash = content.FindHash([sku.Language, sku.LocalizedLanguage], arch == "x86" ? 32 : 64);
        if (hash is null)
        {
            _logger.LogWarning("The page lists no unique SHA-256 for {Language} {Arch}; the download is not checked against Microsoft's table", sku.Language, arch);
        }

        return new DownloadRequest(link.Url)
        {
            ExpectedHashes = hash is null ? [] : [hash],
            LinkResolver = async token =>
            {
                var (fresh, _) = await RequestLinksAsync(route, endpoints, token).ConfigureAwait(false);
                return Pick(fresh, arch, route.Page).Url;
            },
        };
    }

    private static LinkEntry Pick(IReadOnlyList<LinkEntry> links, string architecture, MicrosoftPage page)
    {
        var exact = links.FirstOrDefault(l => string.Equals(l.Architecture, architecture, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        // A single link whose type this code does not recognise can only be the one that was asked for.
        if (links.Count == 1 && links[0].Architecture is null)
        {
            return links[0];
        }

        var offered = string.Join(", ", links.Select(l => l.Architecture ?? "?"));
        throw MicrosoftHttp.Unavailable($"{page.PageUrl} offers no {architecture} download (offered: {offered})");
    }

    private static List<CatalogVariant> Merge(MicrosoftProduct product, List<Offer> offers)
    {
        var variants = new List<CatalogVariant>();

        // The same language on the x64 and Arm64 pages is one variant with two architectures.
        foreach (var group in offers.GroupBy(o => (Edition: o.SingleEdition ? string.Empty : o.Edition.Label, Tag: LanguageTag(o.Sku))))
        {
            var first = group.First();
            var single = group.Key.Edition.Length == 0;

            var properties = new Dictionary<string, string>
            {
                ["format"] = "iso",
                ["language"] = first.Sku.Language,
            };

            foreach (var offer in group)
            {
                foreach (var architecture in offer.Page.Architectures)
                {
                    properties.TryAdd(Route.Key(architecture), Route.Encode(offer.Page, offer.Edition.Id, offer.Sku.Language));
                }
            }

            variants.Add(new CatalogVariant
            {
                Id = single ? group.Key.Tag : $"{Slug(group.Key.Edition)}/{group.Key.Tag}",
                ProductId = product.Id,
                Provider = ProviderId,
                Name = single ? first.Sku.Language : $"{first.Edition.Label} - {first.Sku.Language}",
                Version = WindowsReleases.Describe(WindowsReleases.FindRelease(first.Sku.ProductName), WindowsReleases.FindBuild(first.Sku.ProductName)),
                Language = group.Key.Tag,
                Architectures = ArchitectureChoice.Sort(group.SelectMany(o => o.Page.Architectures)),
                EndOfSupport = product.EndOfSupport,
                Properties = properties,
            });
        }

        return variants;
    }

    private static string LanguageTag(SkuEntry sku) =>
        LanguageTags.FromName(sku.Language) ?? LanguageTags.FromName(sku.LocalizedLanguage) ?? Slug(sku.Language);

    private static string Slug(string text)
    {
        var slug = new StringBuilder();
        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                slug.Append(c);
            }
            else if (slug.Length > 0 && slug[^1] != '-')
            {
                slug.Append('-');
            }
        }

        return slug.ToString().TrimEnd('-');
    }

    private async Task<List<Offer>> ListPageAsync(MicrosoftPage page, CancellationToken cancellationToken)
    {
        var content = await FetchPageAsync(page, cancellationToken).ConfigureAwait(false);
        var endpoints = MicrosoftEndpoints.For(page, content);
        var session = new MicrosoftSession(_http, endpoints, _time, _logger);
        var offers = new List<Offer>();

        foreach (var edition in content.Editions)
        {
            var response = await session.GetSkusAsync(edition.Id, cancellationToken).ConfigureAwait(false);
            ThrowIfRefused(response.Errors, endpoints);

            if (response.Skus.Count == 0)
            {
                _logger.LogWarning("Edition {Edition} ({Id}) on {Page} lists no language", edition.Label, edition.Id, page.Path);
                continue;
            }

            offers.AddRange(response.Skus.Select(s => new Offer(page, edition, s, content.Editions.Count == 1)));
        }

        return offers.Count > 0
            ? offers
            : throw MicrosoftHttp.Unavailable($"{page.PageUrl} lists no language for any of its {content.Editions.Count} edition(s)");
    }

    private async Task<DownloadPage> FetchPageAsync(MicrosoftPage page, CancellationToken cancellationToken)
    {
        var response = await MicrosoftHttp.GetAsync(_http, page.PageUrl, referer: null, MaxPageBytes, cancellationToken).ConfigureAwait(false);

        if (response.Status == HttpStatusCode.Forbidden)
        {
            throw new MicrosoftDownloadBlockedException("HTTP 403", page.ManualUrl, $"{response.Url.Host} answered HTTP 403 for {page.PageUrl}");
        }

        if (response.Status != HttpStatusCode.OK)
        {
            throw MicrosoftHttp.ForStatus(page.PageUrl, response.Status, response.RetryAfter);
        }

        try
        {
            return DownloadPage.Parse(Encoding.UTF8.GetString(response.Body));
        }
        catch (InvalidDataException ex)
        {
            throw MicrosoftHttp.Unavailable($"The download page {page.PageUrl} has an unexpected structure: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Runs the exchange for one language and returns the addresses. If Microsoft refuses the first time, one more
    /// attempt is made in a fresh session that also registers the profiling tag; a second refusal is final.
    /// </summary>
    private async Task<(IReadOnlyList<LinkEntry> Links, SkuEntry Sku)> RequestLinksAsync(Route route, MicrosoftEndpoints endpoints, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var session = new MicrosoftSession(_http, endpoints, _time, _logger) { RegisterProfilingTag = attempt > 0 };

            try
            {
                // The page's script starts the handshake when the page loads, before anyone picks a language.
                await session.HandshakeAsync(cancellationToken).ConfigureAwait(false);

                var skus = await session.GetSkusAsync(route.EditionId, cancellationToken).ConfigureAwait(false);
                ThrowIfRefused(skus.Errors, endpoints);

                var sku = skus.Skus.FirstOrDefault(s => s.Language.Equals(route.Language, StringComparison.OrdinalIgnoreCase))
                    ?? throw MicrosoftHttp.Unavailable(skus.Skus.Count == 0
                        ? $"Edition {route.EditionId} lists no language"
                        : $"'{route.Language}' is no longer offered for edition {route.EditionId}");

                var links = await session.GetLinksAsync(sku.Id, cancellationToken).ConfigureAwait(false);
                ThrowIfRefused(links.Errors, endpoints);

                if (links.Links.Count == 0)
                {
                    throw MicrosoftHttp.Unavailable($"The service returned no download address for {sku.Language}");
                }

                _logger.LogInformation("Got {Count} address(es) for {Language}, valid until {Expires}", links.Links.Count, sku.Language, links.Expires?.ToString("u", CultureInfo.InvariantCulture) ?? "unknown");
                return (links.Links, sku);
            }
            catch (MicrosoftDownloadBlockedException ex) when (attempt == 0)
            {
                _logger.LogInformation("Microsoft refused the request ({Code}); trying once more in a new session", ex.MessageCode);
            }
        }
    }

    private static void ThrowIfRefused(IReadOnlyList<ApiError> errors, MicrosoftEndpoints endpoints)
    {
        if (errors.Count == 0)
        {
            return;
        }

        if (errors.Any(e => e.IsSentinelReject))
        {
            throw new MicrosoftDownloadBlockedException(
                MicrosoftDownloadBlockedException.SentinelCode,
                endpoints.ManualUrl,
                $"Microsoft refused the request ({MicrosoftDownloadBlockedException.SentinelCode}, {ApiError.SentinelRejectKey}). Open {endpoints.ManualUrl} in a browser and download the ISO there.");
        }

        throw MicrosoftHttp.Unavailable($"The download service answered with an error: {string.Join("; ", errors)}");
    }

    private sealed record Offer(MicrosoftPage Page, PageEdition Edition, SkuEntry Sku, bool SingleEdition);

    /// <summary>Where a variant's file comes from: a page, an edition on it, and a language. Stored in the variant's properties.</summary>
    private sealed record Route(MicrosoftPage Page, int EditionId, string Language)
    {
        public static string Key(string architecture) => $"route.{architecture}";

        // The language travels with the route because the pages of one variant may spell it differently.
        public static string Encode(MicrosoftPage page, int editionId, string language) =>
            $"{page.Path}|{editionId.ToString(CultureInfo.InvariantCulture)}|{language}";

        public static Route From(CatalogVariant variant, string architecture)
        {
            if (!variant.Properties.TryGetValue(Key(architecture), out var route)
                || route.Split('|', 3) is not [var path, var edition, var language]
                || MicrosoftProducts.FindPage(path) is not { } page
                || !int.TryParse(edition, NumberStyles.None, CultureInfo.InvariantCulture, out var editionId))
            {
                throw new ArgumentException($"'{variant.Name}' was not produced by this provider.", nameof(variant));
            }

            return new Route(page, editionId, language);
        }
    }
}
