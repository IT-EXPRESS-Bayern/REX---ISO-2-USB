// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;
using Bootrix.Core.Catalog.Distros.Common;
using Bootrix.Core.Net;

namespace Bootrix.Core.Catalog.Distros;

/// <summary>
/// Zorin OS. Its download button asks for a newsletter subscription first and the files are served from a rotating set
/// of volunteer mirrors, with no checksum on zorin.com; there is nothing to verify a download against, so every
/// variant is a manual download (<see cref="CatalogVariant.ManualUrl"/>). The editions and versions are read from the
/// download page, so a new release shows up without an update of Bootrix.
/// </summary>
public sealed partial class ZorinOsProvider : ICatalogProvider
{
    private static readonly Uri DownloadPage = new("https://zorin.com/os/download/");

    private readonly DistroHttp _http;

    public ZorinOsProvider(HttpClient http, TimeProvider? time = null)
    {
        _http = new DistroHttp(http, time);
    }

    public string Id => "zorin";

    public Task<IReadOnlyList<CatalogProduct>> ListProductsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CatalogProduct>>(
        [
            new CatalogProduct
            {
                Id = "zorin",
                Provider = Id,
                Family = CatalogFamily.Linux,
                Name = "Zorin OS",
                Description = "Ubuntu-based desktop that looks like Windows or macOS; the Pro edition is paid",
                Homepage = "https://zorin.com/os/",
                License = "Open source (Core and Education)",
            },
        ]);

    public async Task<IReadOnlyList<CatalogVariant>> ListVariantsAsync(string productId, CancellationToken cancellationToken)
    {
        if (productId != "zorin")
        {
            throw new ArgumentException($"Unknown Zorin product '{productId}'.", nameof(productId));
        }

        var html = await _http.GetStringAsync(DownloadPage, cancellationToken).ConfigureAwait(false);
        var offered = EditionLink().Matches(html)
            .Select(m => (Version: m.Groups["version"].Value, Edition: m.Groups["edition"].Value, Path: m.Value))
            .DistinctBy(x => x.Path)
            .OrderByDescending(x => int.Parse(x.Version, System.Globalization.CultureInfo.InvariantCulture))
            .ThenBy(x => x.Edition == "core" ? 0 : 1)
            .ToList();

        return [.. offered.Select((x, index) => new CatalogVariant
        {
            Id = $"{x.Version}/{x.Edition}",
            ProductId = productId,
            Provider = Id,
            Name = $"Zorin OS {x.Version} {(x.Edition == "core" ? "Core" : "Education")}",
            Version = x.Version,
            Architectures = [Architectures.X64],
            IsRecommended = index == 0,
            ManualUrl = new Uri(DownloadPage, x.Path).AbsoluteUri,
            Properties = VariantProperties.Unsigned(("edition", x.Edition)),
        })];
    }

    public Task<DownloadRequest> ResolveAsync(CatalogVariant variant, string? architecture, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(variant);
        return Task.FromException<DownloadRequest>(
            new NotSupportedException($"'{variant.Name}' can only be downloaded from {variant.ManualUrl}; download it there and choose the file."));
    }

    /// <summary>"/os/download/18/core/": the link behind the page's "Skip to download".</summary>
    [GeneratedRegex(@"/os/download/(?<version>\d+)/(?<edition>core|education)/")]
    private static partial Regex EditionLink();
}
