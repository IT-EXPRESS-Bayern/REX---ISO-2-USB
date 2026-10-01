// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bootrix.Core.Catalog.Distros.Common;
using Bootrix.Core.Net;

namespace Bootrix.Core.Catalog.Distros;

/// <summary>
/// FreeBSD releases for PCs and ARM64 servers. The directory pages of download.freebsd.org list the files with exact
/// sizes; the digests come from the <c>CHECKSUM.SHA256-*</c> file of the release. FreeBSD announces its digests in
/// signed mails but does not sign the files on the server, so they are only as trustworthy as the server over HTTPS.
/// </summary>
public sealed class FreeBsdProvider : ICatalogProvider
{
    private const string Root = "https://download.freebsd.org/releases/ISO-IMAGES/";

    /// <summary>The two supported branches are offered, the newest release of each.</summary>
    private const int BranchesOffered = 2;

    private static readonly (string Root, string? Location)[] Mirrors = [("https://ftp.fau.de/freebsd/releases/ISO-IMAGES/", "DE")];

    private static readonly (string Kind, string Description)[] Kinds =
    [
        ("memstick", "USB memstick image"),
        ("mini-memstick", "USB mini memstick image, installs over the network"),
        ("disc1", "installer ISO"),
        ("dvd1", "full DVD ISO"),
        ("bootonly", "boot-only ISO, installs over the network"),
    ];

    private readonly DistroHttp _http;

    public FreeBsdProvider(HttpClient http, TimeProvider? time = null)
    {
        _http = new DistroHttp(http, time);
    }

    public string Id => "freebsd";

    public Task<IReadOnlyList<CatalogProduct>> ListProductsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CatalogProduct>>(
        [
            new CatalogProduct
            {
                Id = "freebsd",
                Provider = Id,
                Family = CatalogFamily.Bsd,
                Name = "FreeBSD",
                Description = "Unix-like operating system with ZFS and the jails container system",
                Homepage = "https://www.freebsd.org/",
                License = "BSD-2-Clause",
            },
        ]);

    public async Task<IReadOnlyList<CatalogVariant>> ListVariantsAsync(string productId, CancellationToken cancellationToken)
    {
        if (productId != "freebsd")
        {
            throw new ArgumentException($"Unknown FreeBSD product '{productId}'.", nameof(productId));
        }

        var releases = FreeBsdImages.Releases(DirectoryListing.Parse(await _http.GetStringAsync(new Uri(Root), cancellationToken).ConfigureAwait(false)), BranchesOffered);
        var pages = await Task.WhenAll(releases.Select(r => _http.GetStringAsync(new Uri($"{Root}{r}/"), cancellationToken))).ConfigureAwait(false);

        var variants = new List<CatalogVariant>();
        for (var i = 0; i < releases.Count; i++)
        {
            var version = releases[i].ToString();
            var images = FreeBsdImages.Parse(DirectoryListing.Parse(pages[i]), version);

            foreach (var (kind, description) in Kinds)
            {
                var ofKind = images.Where(image => image.Kind == kind).OrderByDescending(image => image.Architecture == Architectures.X64).ToList();
                if (ofKind.Count > 0)
                {
                    variants.Add(Variant(version, kind, description, ofKind, recommended: i == 0 && kind == "memstick"));
                }
            }
        }

        return variants;
    }

    public async Task<DownloadRequest> ResolveAsync(CatalogVariant variant, string? architecture, CancellationToken cancellationToken)
    {
        var arch = Architectures.Select(variant, architecture) ?? throw new ArgumentException("A FreeBSD image needs an architecture.", nameof(architecture));
        var version = variant.Property("version");
        var file = variant.Property($"file.{arch}");
        var vendorArch = variant.Property($"vendorArch.{arch}");

        var sums = await ChecksumSource.UnsignedAsync(_http, new Uri($"{Root}{version}/{FreeBsdImages.ChecksumFileName(version, vendorArch)}"), cancellationToken).ConfigureAwait(false);

        long? size = variant.Properties.TryGetValue($"size.{arch}", out var sizeText) && long.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        return new DownloadRequest(MirrorSources.Build(new Uri($"{Root}{version}/{file}"), Mirrors, $"{version}/{file}"))
        {
            ExpectedHashes = [sums.Pick(file)],
            ExpectedSize = size,
        };
    }

    private CatalogVariant Variant(string version, string kind, string description, List<FreeBsdImage> images, bool recommended)
    {
        var properties = new List<(string Key, string Value)> { ("version", version) };
        foreach (var image in images)
        {
            properties.Add(($"file.{image.Architecture}", image.FileName));
            properties.Add(($"vendorArch.{image.Architecture}", image.VendorArchitecture));
            if (image.Size is { } size)
            {
                properties.Add(($"size.{image.Architecture}", size.ToString(CultureInfo.InvariantCulture)));
            }
        }

        return new CatalogVariant
        {
            Id = $"{version}/{kind}",
            ProductId = "freebsd",
            Provider = Id,
            Name = $"FreeBSD {version} ({description})",
            Version = version,
            Architectures = [.. images.Select(i => i.Architecture)],
            SizeBytes = images[0].Size,
            IsRecommended = recommended,
            Properties = VariantProperties.Unsigned([.. properties]),
        };
    }
}
