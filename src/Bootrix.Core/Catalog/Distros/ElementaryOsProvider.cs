// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Catalog.Distros.Common;
using Bootrix.Core.Errors;
using Bootrix.Core.Net;

namespace Bootrix.Core.Catalog.Distros;

/// <summary>
/// elementary OS. The project asks for a donation before offering the file but links it openly on its front page, each
/// time under an address with a code that is good for three days; the provider reads the current links from that page
/// and renews the address when it is asked to. The <c>.sha256.txt</c> next to the file is not signed, so the digest is
/// only as trustworthy as the download server over HTTPS.
/// </summary>
public sealed class ElementaryOsProvider : ICatalogProvider
{
    private static readonly Uri FrontPage = new("https://elementary.io/");

    private readonly DistroHttp _http;

    public ElementaryOsProvider(HttpClient http, TimeProvider? time = null)
    {
        _http = new DistroHttp(http, time);
    }

    public string Id => "elementary";

    public Task<IReadOnlyList<CatalogProduct>> ListProductsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CatalogProduct>>(
        [
            new CatalogProduct
            {
                Id = "elementary",
                Provider = Id,
                Family = CatalogFamily.Linux,
                Name = "elementary OS",
                Description = "Ubuntu-based desktop with a macOS-like look; pay what you want",
                Homepage = "https://elementary.io/",
                License = "GPL-3.0",
            },
        ]);

    public async Task<IReadOnlyList<CatalogVariant>> ListVariantsAsync(string productId, CancellationToken cancellationToken)
    {
        if (productId != "elementary")
        {
            throw new ArgumentException($"Unknown elementary OS product '{productId}'.", nameof(productId));
        }

        var downloads = ElementaryDownloads.Parse(await _http.GetStringAsync(FrontPage, cancellationToken).ConfigureAwait(false));
        var newest = downloads.Select(d => NumericVersion.Parse(d.Version)).DefaultIfEmpty().Max();

        return [.. downloads
            .GroupBy(d => d.Version)
            .OrderByDescending(g => NumericVersion.Parse(g.Key))
            .Select(g => new CatalogVariant
            {
                Id = g.Key,
                ProductId = productId,
                Provider = Id,
                Name = $"elementary OS {g.Key}",
                Version = g.Key,
                Architectures = [.. g.Select(d => d.Architecture).OrderByDescending(a => a == Architectures.X64)],
                ReleaseDate = g.Max(d => d.Build),
                IsRecommended = NumericVersion.Parse(g.Key).Equals(newest),
                Properties = VariantProperties.Unsigned(("version", g.Key)),
            })];
    }

    public async Task<DownloadRequest> ResolveAsync(CatalogVariant variant, string? architecture, CancellationToken cancellationToken)
    {
        var arch = Architectures.Select(variant, architecture) ?? throw new ArgumentException("An elementary OS image needs an architecture.", nameof(architecture));
        var version = variant.Property("version");

        var download = await FindAsync(version, arch, cancellationToken).ConfigureAwait(false);
        var sums = await ChecksumSource.UnsignedAsync(_http, new Uri(download.Url.AbsoluteUri + ".sha256.txt"), cancellationToken).ConfigureAwait(false);

        return new DownloadRequest(download.Url)
        {
            ExpectedHashes = [sums.Pick(download.FileName)],
            LinkResolver = async token => (await FindAsync(version, arch, token, fresh: true).ConfigureAwait(false)).Url,
        };
    }

    /// <summary>The link of one image on the front page. A fresh read is for renewing an address that has expired.</summary>
    private async Task<ElementaryDownload> FindAsync(string version, string architecture, CancellationToken cancellationToken, bool fresh = false)
    {
        var html = await _http.GetStringAsync(FrontPage, cancellationToken, fresh ? TimeSpan.Zero : null).ConfigureAwait(false);
        return ElementaryDownloads.Parse(html).FirstOrDefault(d => d.Version == version && d.Architecture == architecture)
            ?? throw new BootrixException(ErrorCode.CatalogUnavailable, $"{FrontPage}: elementary OS {version} ({architecture}) is no longer linked");
    }
}
