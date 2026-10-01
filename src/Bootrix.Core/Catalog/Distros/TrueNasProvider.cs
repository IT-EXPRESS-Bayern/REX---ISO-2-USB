// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Catalog.Distros.Common;
using Bootrix.Core.Net;

namespace Bootrix.Core.Catalog.Distros;

/// <summary>
/// TrueNAS (SCALE). The update server's trains say which releases are current; each ISO has a <c>.sha256</c> beside it.
/// iXsystems signs the ISO (a detached .gpg), not the checksum, so the digest is only as trustworthy as the download
/// server over HTTPS and the variants say so.
/// </summary>
public sealed class TrueNasProvider : ICatalogProvider
{
    private static readonly Uri Trains = new("https://update.sys.truenas.net/scale/trains.json");

    private static readonly string[] Hosts = ["https://download.sys.truenas.net/", "https://download.truenas.com/"];

    private readonly DistroHttp _http;

    public TrueNasProvider(HttpClient http, TimeProvider? time = null)
    {
        _http = new DistroHttp(http, time);
    }

    public string Id => "truenas";

    public Task<IReadOnlyList<CatalogProduct>> ListProductsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CatalogProduct>>(
        [
            new CatalogProduct
            {
                Id = "truenas",
                Provider = Id,
                Family = CatalogFamily.Linux,
                Name = "TrueNAS",
                Description = "Storage operating system built around the ZFS file system",
                Homepage = "https://www.truenas.com/",
                License = "Open source",
            },
        ]);

    public async Task<IReadOnlyList<CatalogVariant>> ListVariantsAsync(string productId, CancellationToken cancellationToken)
    {
        if (productId != "truenas")
        {
            throw new ArgumentException($"Unknown TrueNAS product '{productId}'.", nameof(productId));
        }

        var trains = TrueNasReleases.Trains(await _http.GetStringAsync(Trains, cancellationToken).ConfigureAwait(false));
        var manifests = await Task.WhenAll(trains.Select(t => ManifestAsync(t, cancellationToken))).ConfigureAwait(false);

        var releases = trains
            .Zip(manifests, (train, release) => (Train: train, Release: release))
            .Where(x => x.Release is not null)
            .OrderByDescending(x => NumericVersion.Parse(x.Release!.Version))
            .ToList();

        return [.. releases.Select((x, index) => new CatalogVariant
        {
            Id = x.Release!.Version,
            ProductId = productId,
            Provider = Id,
            Name = $"TrueNAS {x.Release.Version} ({TrueNasReleases.Spaced(x.Train.Codename)})",
            Version = x.Release.Version,
            Architectures = [Architectures.X64],
            ReleaseDate = x.Release.Date,
            IsRecommended = index == 0,
            Properties = VariantProperties.Unsigned(("train", x.Train.Name), ("version", x.Release.Version)),
        })];
    }

    public async Task<DownloadRequest> ResolveAsync(CatalogVariant variant, string? architecture, CancellationToken cancellationToken)
    {
        Architectures.Select(variant, architecture);
        var path = $"{variant.Property("train")}/{variant.Property("version")}/TrueNAS-SCALE-{variant.Property("version")}.iso";

        var sums = await ChecksumSource.UnsignedAsync(_http, new Uri(Hosts[0] + path + ".sha256"), cancellationToken).ConfigureAwait(false);
        var sources = Hosts.Select((host, index) => new MirrorSource(new Uri(host + path), index + 1)).ToList();

        // The .sha256 file holds the bare digest; it belongs to the one image next to it.
        return new DownloadRequest(sources) { ExpectedHashes = [sums.Pick(Path.GetFileName(path))] };
    }

    private async Task<TrueNasRelease?> ManifestAsync(TrueNasTrain train, CancellationToken cancellationToken)
    {
        var json = await _http.TryGetStringAsync(new Uri($"https://update.sys.truenas.net/scale/{train.Name}/manifest.json"), cancellationToken).ConfigureAwait(false);
        return json is null ? null : TrueNasReleases.Manifest(json);
    }
}
