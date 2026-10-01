// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;
using Bootrix.Core.Catalog.Distros.Common;
using Bootrix.Core.Net;

namespace Bootrix.Core.Catalog.Distros;

/// <summary>
/// GParted Live, the stable branch. gparted.org publishes the signed CHECKSUMS.TXT (MD5, SHA-1, SHA-256 and SHA-512
/// sections); the images themselves are hosted on SourceForge.
/// </summary>
public sealed partial class GPartedLiveProvider : ICatalogProvider
{
    private const string Site = "https://gparted.org/gparted-live/stable/";
    private const string SourceForge = "https://downloads.sourceforge.net/project/gparted/gparted-live-stable/";

    private readonly DistroHttp _http;

    public GPartedLiveProvider(HttpClient http, TimeProvider? time = null)
    {
        _http = new DistroHttp(http, time);
    }

    public string Id => "gparted";

    public Task<IReadOnlyList<CatalogProduct>> ListProductsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CatalogProduct>>(
        [
            new CatalogProduct
            {
                Id = "gparted-live",
                Provider = Id,
                Family = CatalogFamily.Rescue,
                Name = "GParted Live",
                Description = "Partition editor on a bootable disc",
                Homepage = "https://gparted.org/",
                License = "GPL-2.0",
            },
        ]);

    public async Task<IReadOnlyList<CatalogVariant>> ListVariantsAsync(string productId, CancellationToken cancellationToken)
    {
        if (productId != "gparted-live")
        {
            throw new ArgumentException($"Unknown GParted product '{productId}'.", nameof(productId));
        }

        var sums = await ChecksumSource.UnsignedAsync(_http, new Uri(Site + "CHECKSUMS.TXT"), cancellationToken).ConfigureAwait(false);

        // The same ISO is listed in every digest section; the zip is a repackaging for manual setup and is not offered.
        var images = sums.Entries
            .Select(e => ImageName().Match(e.FileName ?? string.Empty))
            .Where(m => m.Success && Architectures.FromVendorName(m.Groups["arch"].Value) is not null)
            .DistinctBy(m => m.Value)
            .ToList();

        return [.. images.Select((m, index) => new CatalogVariant
        {
            Id = $"{m.Groups["version"].Value}/{m.Groups["arch"].Value}",
            ProductId = productId,
            Provider = Id,
            Name = $"GParted Live {m.Groups["version"].Value}",
            Version = m.Groups["version"].Value,
            Architectures = [Architectures.FromVendorName(m.Groups["arch"].Value)!],
            IsRecommended = index == 0,
            Properties = VariantProperties.Signed(("version", m.Groups["version"].Value), ("file", m.Value)),
        })];
    }

    public async Task<DownloadRequest> ResolveAsync(CatalogVariant variant, string? architecture, CancellationToken cancellationToken)
    {
        Architectures.Select(variant, architecture);
        var file = variant.Property("file");

        var sums = await ChecksumSource.DetachedAsync(
            _http,
            new Uri(Site + "CHECKSUMS.TXT"),
            new Uri(Site + "CHECKSUMS.TXT.gpg"),
            DistroKeys.GParted,
            cancellationToken).ConfigureAwait(false);

        return new DownloadRequest(new Uri($"{SourceForge}{variant.Property("version")}/{file}")) { ExpectedHashes = [sums.Pick(file)] };
    }

    [GeneratedRegex(@"^gparted-live-(?<version>\d+(?:\.\d+)*-\d+)-(?<arch>amd64|i686|i686-pae)\.iso$")]
    private static partial Regex ImageName();
}
