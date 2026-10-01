// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Catalog.Distros.Common;
using Bootrix.Core.Net;

namespace Bootrix.Core.Catalog.Distros;

/// <summary>
/// Ubuntu and the official flavours that publish their images in the same layout (a signed SHA256SUMS next to the
/// ISOs). Releases come from Canonical's meta-release list; the file names and digests from the checksum file of
/// each release directory.
/// </summary>
public sealed class UbuntuProvider : ICatalogProvider
{
    private const int StandardSupportYears = 5;

    private static readonly Uri MetaRelease = new("https://changelogs.ubuntu.com/meta-release");

    private static readonly (string Root, string? Location)[] ReleaseMirrors =
    [
        ("https://ftp.fau.de/ubuntu-releases/", "DE"),
        ("https://ftp.halifax.rwth-aachen.de/ubuntu-releases/", "DE"),
        ("https://mirrors.edge.kernel.org/ubuntu-releases/", "US"),
        ("https://mirror.us.leaseweb.net/ubuntu-releases/", "US"),
    ];

    private static readonly Flavour[] Flavours =
    [
        new("ubuntu", "Ubuntu", "ubuntu", "Desktop and server Linux from Canonical", series => $"https://releases.ubuntu.com/{series}/"),
        new("kubuntu", "Kubuntu", "kubuntu", "Ubuntu with the KDE Plasma desktop", series => CdImage("kubuntu", series)),
        new("xubuntu", "Xubuntu", "xubuntu", "Ubuntu with the lightweight Xfce desktop", series => CdImage("xubuntu", series)),
        new("lubuntu", "Lubuntu", "lubuntu", "Ubuntu with the lightweight LXQt desktop", series => CdImage("lubuntu", series)),
        new("ubuntu-mate", "Ubuntu MATE", "ubuntu-mate", "Ubuntu with the traditional MATE desktop", series => CdImage("ubuntu-mate", series)),
    ];

    private readonly DistroHttp _http;

    public UbuntuProvider(HttpClient http, TimeProvider? time = null)
    {
        _http = new DistroHttp(http, time);
    }

    public string Id => "ubuntu";

    public Task<IReadOnlyList<CatalogProduct>> ListProductsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CatalogProduct>>([.. Flavours.Select(f => new CatalogProduct
        {
            Id = f.ProductId,
            Provider = Id,
            Family = CatalogFamily.Linux,
            Name = f.Name,
            Description = f.Description,
            Homepage = f.ProductId == "ubuntu" ? "https://ubuntu.com/" : null,
            License = "Open source",
        })]);

    public async Task<IReadOnlyList<CatalogVariant>> ListVariantsAsync(string productId, CancellationToken cancellationToken)
    {
        var flavour = Flavours.FirstOrDefault(f => f.ProductId == productId)
            ?? throw new ArgumentException($"Unknown Ubuntu product '{productId}'.", nameof(productId));

        // The meta-release list keeps flagging an LTS as supported while it is only in paid extended maintenance;
        // the five years of standard support are what a fresh install should still get.
        var cutoff = DateOnly.FromDateTime(_http.Time.GetUtcNow().UtcDateTime).AddYears(-StandardSupportYears);
        var releases = UbuntuReleases.ParseMetaRelease(await _http.GetStringAsync(MetaRelease, cancellationToken).ConfigureAwait(false))
            .Where(r => r.Supported && (r.Date is null || r.Date > cutoff))
            .OrderByDescending(r => r.Version)
            .ToList();
        var newestLts = releases.Where(r => r.IsLts).Select(r => r.Version).DefaultIfEmpty().Max();

        var variants = new List<CatalogVariant>();
        foreach (var release in releases)
        {
            var baseUrl = flavour.BaseUrl(release.Series);
            var sums = await _http.TryGetBytesAsync(new Uri(baseUrl + "SHA256SUMS"), cancellationToken).ConfigureAwait(false);
            if (sums is null)
            {
                // A flavour that skipped a release has no directory for it.
                continue;
            }

            foreach (var image in UbuntuReleases.LatestImages(ChecksumFile.Parse(sums), flavour.FilePrefix))
            {
                variants.Add(new CatalogVariant
                {
                    Id = $"{image.Version}/{image.Kind}",
                    ProductId = flavour.ProductId,
                    Provider = Id,
                    Name = $"{flavour.Name} {image.Version}{(release.IsLts ? " LTS" : string.Empty)} {(image.Kind == "desktop" ? "Desktop" : "Server")}",
                    Version = image.Version.ToString(),
                    Architectures = [Architectures.X64],
                    ReleaseDate = release.Date,
                    IsRecommended = release.IsLts && release.Version.Equals(newestLts) && image.Kind == "desktop",
                    Properties = VariantProperties.Signed(("base", baseUrl), ("file", image.FileName), ("series", release.Series)),
                });
            }
        }

        return variants;
    }

    public async Task<DownloadRequest> ResolveAsync(CatalogVariant variant, string? architecture, CancellationToken cancellationToken)
    {
        Architectures.Select(variant, architecture);
        var baseUrl = variant.Property("base");
        var file = variant.Property("file");

        var sums = await ChecksumSource.DetachedAsync(
            _http,
            new Uri(baseUrl + "SHA256SUMS"),
            new Uri(baseUrl + "SHA256SUMS.gpg"),
            DistroKeys.Ubuntu,
            cancellationToken).ConfigureAwait(false);

        // Only releases.ubuntu.com has the mirror tree; the flavours' cdimage server is the single source.
        var mirrors = baseUrl.StartsWith("https://releases.ubuntu.com/", StringComparison.Ordinal) ? ReleaseMirrors : [];
        return new DownloadRequest(MirrorSources.Build(new Uri(baseUrl + file), mirrors, $"{variant.Property("series")}/{file}"))
        {
            ExpectedHashes = [sums.Pick(file)],
        };
    }

    private static string CdImage(string flavour, string series) =>
        $"https://cdimage.ubuntu.com/{flavour}/releases/{series}/release/";

    private sealed record Flavour(string ProductId, string Name, string FilePrefix, string Description, Func<string, string> BaseUrl);
}
