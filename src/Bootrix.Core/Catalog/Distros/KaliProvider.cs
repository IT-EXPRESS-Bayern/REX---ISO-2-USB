// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Catalog.Distros.Common;
using Bootrix.Core.Net;

namespace Bootrix.Core.Catalog.Distros;

/// <summary>
/// Kali Linux's current release. The directory page names the images that can be downloaded; the digests come from the
/// SHA256SUMS next to them, signed with Kali's archive key. cdimage.kali.org redirects every request to a nearby
/// mirror, so it is the one address to give out.
/// </summary>
public sealed class KaliProvider : ICatalogProvider
{
    private const string Base = "https://cdimage.kali.org/current/";

    private static readonly Dictionary<string, string> KindNames = new(StringComparer.Ordinal)
    {
        ["installer"] = "Installer",
        ["installer-netinst"] = "Netinst (network installer)",
        ["installer-purple"] = "Purple (defensive tools)",
        ["installer-everything"] = "Installer (all tools)",
        ["live"] = "Live",
        ["live-everything"] = "Live (all tools)",
    };

    private readonly DistroHttp _http;

    public KaliProvider(HttpClient http, TimeProvider? time = null)
    {
        _http = new DistroHttp(http, time);
    }

    public string Id => "kali";

    public Task<IReadOnlyList<CatalogProduct>> ListProductsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CatalogProduct>>(
        [
            new CatalogProduct
            {
                Id = "kali",
                Provider = Id,
                Family = CatalogFamily.Linux,
                Name = "Kali Linux",
                Description = "Debian-based distribution for security testing and forensics",
                Homepage = "https://www.kali.org/",
                License = "Open source",
            },
        ]);

    public async Task<IReadOnlyList<CatalogVariant>> ListVariantsAsync(string productId, CancellationToken cancellationToken)
    {
        if (productId != "kali")
        {
            throw new ArgumentException($"Unknown Kali product '{productId}'.", nameof(productId));
        }

        var images = KaliImages.Parse(DirectoryListing.Parse(await _http.GetStringAsync(new Uri(Base), cancellationToken).ConfigureAwait(false)));
        var newest = images.Count == 0 ? null : images[0].Version;

        return [.. images.Select(image => new CatalogVariant
        {
            Id = $"{image.Version}/{image.Kind}",
            ProductId = productId,
            Provider = Id,
            Name = $"Kali Linux {image.Version} {KindNames.GetValueOrDefault(image.Kind, image.Kind)}",
            Version = image.Version,
            Architectures = image.Architectures,
            IsRecommended = image.Version == newest && image.Kind == "installer",
            Properties = VariantProperties.Signed(("version", image.Version), ("kind", image.Kind)),
        })];
    }

    public async Task<DownloadRequest> ResolveAsync(CatalogVariant variant, string? architecture, CancellationToken cancellationToken)
    {
        var arch = Architectures.Select(variant, architecture);
        var file = KaliImages.FileName(variant.Property("version"), variant.Property("kind"), arch == Architectures.Arm64 ? "arm64" : "amd64");

        var sums = await ChecksumSource.DetachedAsync(
            _http,
            new Uri(Base + "SHA256SUMS"),
            new Uri(Base + "SHA256SUMS.gpg"),
            DistroKeys.Kali,
            cancellationToken).ConfigureAwait(false);

        return new DownloadRequest(new Uri(Base + file)) { ExpectedHashes = [sums.Pick(file)] };
    }
}
