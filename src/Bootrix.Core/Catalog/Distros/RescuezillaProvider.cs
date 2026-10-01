// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Catalog.Distros.Common;
using Bootrix.Core.Errors;
using Bootrix.Core.Net;

namespace Bootrix.Core.Catalog.Distros;

/// <summary>
/// Rescuezilla, the graphical front end for Clonezilla. The project's download page names the current release; its
/// <c>SHA256SUM</c> asset on GitHub lists every ISO built for it, each on a different Ubuntu base. The sums are not
/// signed, so the digest is only as trustworthy as GitHub over HTTPS.
/// </summary>
public sealed class RescuezillaProvider : ICatalogProvider
{
    private static readonly Uri DownloadPage = new("https://rescuezilla.com/download");

    private readonly DistroHttp _http;

    public RescuezillaProvider(HttpClient http, TimeProvider? time = null)
    {
        _http = new DistroHttp(http, time);
    }

    public string Id => "rescuezilla";

    public Task<IReadOnlyList<CatalogProduct>> ListProductsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CatalogProduct>>(
        [
            new CatalogProduct
            {
                Id = "rescuezilla",
                Provider = Id,
                Family = CatalogFamily.Rescue,
                Name = "Rescuezilla",
                Description = "Backup and restore of whole disks with a graphical interface, based on Clonezilla",
                Homepage = "https://rescuezilla.com/",
                License = "GPL-3.0",
            },
        ]);

    public async Task<IReadOnlyList<CatalogVariant>> ListVariantsAsync(string productId, CancellationToken cancellationToken)
    {
        if (productId != "rescuezilla")
        {
            throw new ArgumentException($"Unknown Rescuezilla product '{productId}'.", nameof(productId));
        }

        var release = await CurrentReleaseAsync(cancellationToken).ConfigureAwait(false);
        var sums = await ChecksumSource.UnsignedAsync(_http, RescuezillaReleases.AssetUrl(release.Tag, "SHA256SUM"), cancellationToken).ConfigureAwait(false);

        // The recommended image first, then the newest Ubuntu bases; Ubuntu codenames run alphabetically through the years.
        return [.. RescuezillaReleases.Images(sums)
            .OrderByDescending(i => i.FileName == release.RecommendedFile)
            .ThenByDescending(i => i.Architecture == Architectures.X64)
            .ThenByDescending(i => i.Base, StringComparer.Ordinal)
            .Select(image => new CatalogVariant
            {
                Id = $"{image.Version}/{image.Architecture}-{image.Base}",
                ProductId = productId,
                Provider = Id,
                Name = $"Rescuezilla {image.Version} (Ubuntu {image.Base} base{(image.Architecture == Architectures.I386 ? ", 32-bit" : string.Empty)})",
                Version = image.Version,
                Architectures = [image.Architecture],
                IsRecommended = image.FileName == release.RecommendedFile,
                Properties = VariantProperties.Unsigned(("tag", release.Tag), ("file", image.FileName)),
            })];
    }

    public async Task<DownloadRequest> ResolveAsync(CatalogVariant variant, string? architecture, CancellationToken cancellationToken)
    {
        Architectures.Select(variant, architecture);
        var tag = variant.Property("tag");
        var file = variant.Property("file");

        var sums = await ChecksumSource.UnsignedAsync(_http, RescuezillaReleases.AssetUrl(tag, "SHA256SUM"), cancellationToken).ConfigureAwait(false);
        return new DownloadRequest(RescuezillaReleases.AssetUrl(tag, file)) { ExpectedHashes = [sums.Pick(file)] };
    }

    private async Task<RescuezillaRelease> CurrentReleaseAsync(CancellationToken cancellationToken) =>
        RescuezillaReleases.ParseDownloadPage(await _http.GetStringAsync(DownloadPage, cancellationToken).ConfigureAwait(false))
        ?? throw new BootrixException(ErrorCode.CatalogUnavailable, $"{DownloadPage}: no link to a release found");
}
