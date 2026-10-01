// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bootrix.Core.Catalog.Distros.Common;
using Bootrix.Core.Errors;
using Bootrix.Core.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Core.Catalog.Distros;

/// <summary>
/// Fedora's editions and spins. The image list is Fedora's <c>releases.json</c>; the digest comes from the clear-signed
/// CHECKSUM file next to the image (signed by that release's own key), and the mirrors from MirrorManager's metalink.
/// A release whose key Bootrix does not hold yet is not offered, because it could not be verified.
/// </summary>
public sealed class FedoraProvider : ICatalogProvider
{
    /// <summary>Fedora supports the latest release and the one before it; an older release is moved to the archive.</summary>
    private const int SupportedReleases = 2;

    private static readonly Uri ReleasesJson = new("https://fedoraproject.org/releases.json");

    private static readonly Edition[] Editions =
    [
        new("fedora-workstation", "Fedora Workstation", "Workstation", "GNOME desktop for everyday use and development"),
        new("fedora-kde", "Fedora KDE Plasma Desktop", "KDE", "Fedora with the KDE Plasma desktop"),
        new("fedora-server", "Fedora Server", "Server", "Server installer with web-based management"),
        new("fedora-spins", "Fedora Spins", "Spins", "Fedora with other desktops such as Xfce, Cinnamon and MATE"),
        new("fedora-everything", "Fedora Everything", "Everything", "Small network installer to choose the packages yourself"),
    ];

    private static readonly Dictionary<string, string> SpinNames = new(StringComparer.Ordinal)
    {
        ["Mate"] = "MATE",
        ["SoaS"] = "Sugar on a Stick",
        ["KDE_Mobile"] = "KDE Plasma Mobile",
        ["MiracleWM"] = "Miracle WM",
        ["i3"] = "i3 window manager",
    };

    private readonly DistroHttp _http;
    private readonly ILogger _logger;

    public FedoraProvider(HttpClient http, TimeProvider? time = null, ILogger<FedoraProvider>? logger = null)
    {
        _http = new DistroHttp(http, time);
        _logger = logger ?? NullLogger<FedoraProvider>.Instance;
    }

    public string Id => "fedora";

    public Task<IReadOnlyList<CatalogProduct>> ListProductsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CatalogProduct>>([.. Editions.Select(e => new CatalogProduct
        {
            Id = e.ProductId,
            Provider = Id,
            Family = CatalogFamily.Linux,
            Name = e.Name,
            Description = e.Description,
            Homepage = "https://fedoraproject.org/",
            License = "Open source",
        })]);

    public async Task<IReadOnlyList<CatalogVariant>> ListVariantsAsync(string productId, CancellationToken cancellationToken)
    {
        var edition = Editions.FirstOrDefault(e => e.ProductId == productId)
            ?? throw new ArgumentException($"Unknown Fedora product '{productId}'.", nameof(productId));

        var all = FedoraReleases.Parse(await _http.GetStringAsync(ReleasesJson, cancellationToken).ConfigureAwait(false));

        // The list also keeps releases that have been retired to the archive; Fedora supports the two newest final ones.
        var supported = all.Select(i => i.Release).Distinct().OrderDescending().Take(SupportedReleases).ToList();
        var images = all.Where(i => i.Variant == edition.Variant && supported.Contains(i.Release) && DistroKeys.HasFedoraKey(i.Release)).ToList();
        var newest = images.Count == 0 ? 0 : images.Max(i => i.Release);

        return [.. images
            .GroupBy(i => (i.Release, i.SubVariant, i.Kind))
            .OrderByDescending(g => g.Key.Release)
            .ThenBy(g => g.Key.SubVariant, StringComparer.Ordinal)
            .ThenBy(g => g.Key.Kind, StringComparer.Ordinal)
            .Select(g => Variant(edition, g.ToList(), newest))];
    }

    public async Task<DownloadRequest> ResolveAsync(CatalogVariant variant, string? architecture, CancellationToken cancellationToken)
    {
        var arch = Architectures.Select(variant, architecture) ?? throw new ArgumentException("A Fedora image needs an architecture.", nameof(architecture));
        var release = int.Parse(variant.Property("release"), CultureInfo.InvariantCulture);
        var link = new Uri(variant.Property($"link.{arch}"));
        var file = variant.Property($"file.{arch}");

        // The checksum file of an image sits in its directory on Fedora's own server, not on whichever mirror the redirector picks.
        var directory = new UriBuilder(link) { Host = "dl.fedoraproject.org" }.Uri;
        var checksumName = $"Fedora-{variant.Property("variant")}-{release}-{variant.Property($"compose.{arch}")}-{variant.Property($"vendorArch.{arch}")}-CHECKSUM";
        var checksum = new Uri(directory, checksumName);
        var sums = await ChecksumSource.ClearSignedAsync(_http, checksum, DistroKeys.FedoraFor(release), cancellationToken).ConfigureAwait(false);
        var hash = sums.Pick(file);

        long? size = variant.Properties.TryGetValue($"size.{arch}", out var sizeText) && long.TryParse(sizeText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        var entry = await MirrorsAsync(link, file, hash, cancellationToken).ConfigureAwait(false);
        return new DownloadRequest(entry is null ? [new MirrorSource(link)] : MirrorSources.Pick(entry.Mirrors))
        {
            ExpectedHashes = [hash],
            ExpectedSize = entry?.Size ?? size,
        };
    }

    /// <summary>
    /// MirrorManager's list of mirrors for exactly this file, or null when it is unusable. A metalink whose digest
    /// disagrees with the signed one belongs to a different build, so its mirrors are not trusted either.
    /// </summary>
    private async Task<MetalinkFile?> MirrorsAsync(Uri link, string file, FileHash signed, CancellationToken cancellationToken)
    {
        var path = string.Join('/', link.AbsolutePath.TrimStart('/').Split('/').Select(Uri.EscapeDataString));
        var metalink = new Uri($"https://mirrors.fedoraproject.org/metalink?path={path}");
        try
        {
            var entry = MetalinkDocument.Parse(await _http.GetStringAsync(metalink, cancellationToken).ConfigureAwait(false)).Find(file);
            if (entry is null || entry.Hashes.Any(h => h.Kind == signed.Kind && h.Hex != signed.Hex))
            {
                _logger.LogWarning("Fedora metalink for {File} does not match the signed digest; using the redirector only", file);
                return null;
            }

            return entry.Mirrors.Count == 0 ? null : entry;
        }
        catch (BootrixException ex) when (ex.Code is ErrorCode.CatalogUnavailable or ErrorCode.MetalinkInvalid)
        {
            // Mirrors are an optimisation; the redirector at download.fedoraproject.org still serves the file.
            _logger.LogWarning(ex, "Fedora mirror list for {File} is unavailable", file);
            return null;
        }
    }

    private CatalogVariant Variant(Edition edition, List<FedoraImage> images, int newestRelease)
    {
        var first = images[0];
        var properties = new List<(string Key, string Value)>
        {
            ("release", first.Release.ToString(CultureInfo.InvariantCulture)),
            ("variant", first.Variant),
        };

        foreach (var image in images)
        {
            properties.Add(($"link.{image.Architecture}", image.Link.AbsoluteUri));
            properties.Add(($"file.{image.Architecture}", image.FileName));
            properties.Add(($"compose.{image.Architecture}", image.Compose));
            properties.Add(($"vendorArch.{image.Architecture}", image.VendorArchitecture));
            if (image.Size is { } size)
            {
                properties.Add(($"size.{image.Architecture}", size.ToString(CultureInfo.InvariantCulture)));
            }
        }

        var architectures = images.Select(i => i.Architecture).Distinct().Order(StringComparer.Ordinal).ToList();
        var x64 = images.FirstOrDefault(i => i.Architecture == Architectures.X64);
        return new CatalogVariant
        {
            Id = $"{first.Release}/{first.SubVariant}/{first.Kind}",
            ProductId = edition.ProductId,
            Provider = Id,
            Name = Name(edition, first),
            Version = first.Release.ToString(CultureInfo.InvariantCulture),
            Architectures = [.. architectures.OrderByDescending(a => a == Architectures.X64)],
            SizeBytes = x64?.Size ?? first.Size,
            IsRecommended = IsRecommended(edition, first, newestRelease),
            Properties = VariantProperties.Signed([.. properties]),
        };
    }

    /// <summary>The newest release of each edition, in the form Fedora's download page leads with: DVD for Server, the live image elsewhere.</summary>
    private static bool IsRecommended(Edition edition, FedoraImage image, int newestRelease) =>
        image.Release == newestRelease
        && edition.Variant != "Spins"
        && (image.Kind != "netinst" || edition.Variant == "Everything");

    private static string Name(Edition edition, FedoraImage image) => edition.Variant switch
    {
        "Spins" => $"Fedora {SpinNames.GetValueOrDefault(image.SubVariant, image.SubVariant.Replace('_', ' '))} {image.Release}",
        "Server" => $"Fedora Server {image.Release} ({(image.Kind == "dvd" ? "DVD" : "netinst")})",
        "Everything" => $"Fedora Everything {image.Release} (netinst)",
        _ => $"{edition.Name} {image.Release}",
    };

    private sealed record Edition(string ProductId, string Name, string Variant, string Description);
}
