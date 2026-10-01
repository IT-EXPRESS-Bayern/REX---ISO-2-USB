// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Catalog.Distros.Common;
using Bootrix.Core.Net;

namespace Bootrix.Core.Catalog.Distros;

/// <summary>
/// Linux Mint's three editions. linuxmint.com sits behind a bot check, so releases and files come from the kernel.org
/// mirror, which carries the same tree including the signed sha256sum.txt of every release.
/// </summary>
public sealed class LinuxMintProvider : ICatalogProvider
{
    /// <summary>Mint 21 and 22 are both inside their support window; older series are not offered.</summary>
    private const int SeriesOffered = 2;

    private const string Primary = "https://mirrors.edge.kernel.org/linuxmint/stable/";

    private static readonly (string Root, string? Location)[] Mirrors =
    [
        ("https://ftp.fau.de/mint/iso/stable/", "DE"),
        ("https://mirror.dogado.de/linuxmint-cd/stable/", "DE"),
        ("https://mirror.csclub.uwaterloo.ca/linuxmint/stable/", "CA"),
        ("https://mirror.rackspace.com/linuxmint/iso/stable/", "US"),
    ];

    private readonly DistroHttp _http;

    public LinuxMintProvider(HttpClient http, TimeProvider? time = null)
    {
        _http = new DistroHttp(http, time);
    }

    public string Id => "mint";

    public Task<IReadOnlyList<CatalogProduct>> ListProductsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CatalogProduct>>(
        [
            new CatalogProduct
            {
                Id = "linuxmint",
                Provider = Id,
                Family = CatalogFamily.Linux,
                Name = "Linux Mint",
                Description = "Beginner-friendly desktop Linux based on Ubuntu",
                Homepage = "https://linuxmint.com/",
                License = "Open source",
            },
        ]);

    public async Task<IReadOnlyList<CatalogVariant>> ListVariantsAsync(string productId, CancellationToken cancellationToken)
    {
        if (productId != "linuxmint")
        {
            throw new ArgumentException($"Unknown Linux Mint product '{productId}'.", nameof(productId));
        }

        var listing = DirectoryListing.Parse(await _http.GetStringAsync(new Uri(Primary), cancellationToken).ConfigureAwait(false));
        var releases = LinuxMintImages.NewestOfEachSeries(LinuxMintImages.ReleaseDirectories(listing), SeriesOffered);

        var newest = releases.Count > 0 ? releases[0] : null;
        var variants = new List<CatalogVariant>();
        foreach (var release in releases)
        {
            var sums = await ChecksumSource.UnsignedAsync(_http, new Uri($"{Primary}{release}/sha256sum.txt"), cancellationToken).ConfigureAwait(false);
            foreach (var image in LinuxMintImages.Parse(sums).OrderBy(i => i.IsEdge).ThenBy(i => i.Edition, StringComparer.Ordinal))
            {
                variants.Add(Variant(image, release.Equals(newest) && image is { Edition: "cinnamon", IsEdge: false }));
            }
        }

        return variants;
    }

    public async Task<DownloadRequest> ResolveAsync(CatalogVariant variant, string? architecture, CancellationToken cancellationToken)
    {
        Architectures.Select(variant, architecture);
        var release = variant.Property("release");
        var file = variant.Property("file");

        var sums = await ChecksumSource.DetachedAsync(
            _http,
            new Uri($"{Primary}{release}/sha256sum.txt"),
            new Uri($"{Primary}{release}/sha256sum.txt.gpg"),
            DistroKeys.LinuxMint,
            cancellationToken).ConfigureAwait(false);

        var path = $"{release}/{file}";
        return new DownloadRequest(MirrorSources.Build(new Uri(Primary + path), Mirrors, path))
        {
            ExpectedHashes = [sums.Pick(file)],
        };
    }

    private CatalogVariant Variant(LinuxMintImage image, bool recommended)
    {
        var edition = image.Edition switch
        {
            "mate" => "MATE",
            "xfce" => "Xfce",
            _ => "Cinnamon",
        };

        return new CatalogVariant
        {
            Id = $"{image.Version}/{image.Edition}{(image.IsEdge ? "-edge" : string.Empty)}",
            ProductId = "linuxmint",
            Provider = Id,
            Name = $"Linux Mint {image.Version} {edition}{(image.IsEdge ? " (Edge)" : string.Empty)}",
            Version = image.Version.ToString(),
            Architectures = [Architectures.X64],
            IsRecommended = recommended,
            Properties = VariantProperties.Signed(("release", image.Version.ToString()), ("file", image.FileName)),
        };
    }
}
