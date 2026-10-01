// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bootrix.Core.Catalog.Distros.Common;
using Bootrix.Core.Errors;
using Bootrix.Core.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Core.Catalog.Distros;

/// <summary>
/// The monthly Arch Linux ISO. Arch signs the ISO itself (a detached .sig) and publishes no signed checksum file, so
/// the digest from archlinux.org is only as trustworthy as that HTTPS site; the variants say so. The fingerprint of
/// the release key and the signature's address are kept in the variant's properties.
/// </summary>
public sealed class ArchLinuxProvider : ICatalogProvider
{
    private const int MirrorLimit = MirrorSources.DefaultLimit - 1;

    private static readonly Uri ReleasesJson = new("https://archlinux.org/releng/releases/json/");
    private static readonly Uri MirrorStatus = new("https://archlinux.org/mirrors/status/json/");
    private static readonly Uri GeoMirror = new("https://geo.mirror.pkgbuild.com/");

    private readonly DistroHttp _http;
    private readonly ILogger _logger;

    public ArchLinuxProvider(HttpClient http, TimeProvider? time = null, ILogger<ArchLinuxProvider>? logger = null)
    {
        _http = new DistroHttp(http, time);
        _logger = logger ?? NullLogger<ArchLinuxProvider>.Instance;
    }

    public string Id => "arch";

    public Task<IReadOnlyList<CatalogProduct>> ListProductsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CatalogProduct>>(
        [
            new CatalogProduct
            {
                Id = "archlinux",
                Provider = Id,
                Family = CatalogFamily.Linux,
                Name = "Arch Linux",
                Description = "Rolling-release distribution that you assemble yourself",
                Homepage = "https://archlinux.org/",
                License = "Open source",
            },
        ]);

    public async Task<IReadOnlyList<CatalogVariant>> ListVariantsAsync(string productId, CancellationToken cancellationToken)
    {
        if (productId != "archlinux")
        {
            throw new ArgumentException($"Unknown Arch Linux product '{productId}'.", nameof(productId));
        }

        var releases = ArchReleases.Parse(await _http.GetStringAsync(ReleasesJson, cancellationToken).ConfigureAwait(false));
        return [.. releases.Select((release, index) => new CatalogVariant
        {
            Id = release.Version,
            ProductId = productId,
            Provider = Id,
            Name = $"Arch Linux {release.Version}",
            Version = release.Version,
            Architectures = [Architectures.X64],
            SizeBytes = release.Size,
            ReleaseDate = release.ReleaseDate,
            IsRecommended = index == 0,
            Properties = VariantProperties.Unsigned(
                ("path", release.IsoPath),
                ("sha256", release.Sha256.Hex),
                ("size", release.Size?.ToString(CultureInfo.InvariantCulture) ?? string.Empty),
                ("signature", release.IsoPath + ".sig"),
                ("pgpFingerprint", release.PgpFingerprint ?? string.Empty)),
        })];
    }

    public async Task<DownloadRequest> ResolveAsync(CatalogVariant variant, string? architecture, CancellationToken cancellationToken)
    {
        Architectures.Select(variant, architecture);
        var path = variant.Property("path");
        var sha256 = new FileHash(HashKind.Sha256, variant.Property("sha256"));
        long? size = long.TryParse(variant.Property("size"), NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

        var sources = new List<MirrorSource> { new(new Uri(GeoMirror, path), 1) };
        foreach (var mirror in await MirrorsAsync(cancellationToken).ConfigureAwait(false))
        {
            sources.Add(mirror with { Url = new Uri(mirror.Url, path) });
        }

        return new DownloadRequest(sources) { ExpectedHashes = [sha256], ExpectedSize = size };
    }

    /// <summary>Mirrors only speed things up; without the status page the vendor's geo-routed address alone still works.</summary>
    private async Task<IReadOnlyList<MirrorSource>> MirrorsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return ArchReleases.ParseMirrors(await _http.GetStringAsync(MirrorStatus, cancellationToken).ConfigureAwait(false), MirrorLimit);
        }
        catch (BootrixException ex) when (ex.Code == ErrorCode.CatalogUnavailable)
        {
            _logger.LogWarning(ex, "Arch Linux mirror status is unavailable; using the geo-routed mirror only");
            return [];
        }
    }
}
