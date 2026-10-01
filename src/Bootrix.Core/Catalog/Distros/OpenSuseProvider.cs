// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Catalog.Distros.Common;
using Bootrix.Core.Errors;
using Bootrix.Core.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Core.Catalog.Distros;

/// <summary>
/// openSUSE Leap and Tumbleweed. The digest comes from the checksum file next to the image, whose detached signature
/// is checked against the project's signing key; the mirrors and the exact size from the image's <c>.meta4</c>
/// (MirrorCache), which is used only if it announces the same digest.
/// </summary>
public sealed class OpenSuseProvider : ICatalogProvider
{
    private const string Site = "https://download.opensuse.org/";

    private static readonly Uri Distributions = new("https://get.opensuse.org/api/v0/distributions.json");

    private readonly DistroHttp _http;
    private readonly ILogger _logger;

    public OpenSuseProvider(HttpClient http, TimeProvider? time = null, ILogger<OpenSuseProvider>? logger = null)
    {
        _http = new DistroHttp(http, time);
        _logger = logger ?? NullLogger<OpenSuseProvider>.Instance;
    }

    public string Id => "opensuse";

    public Task<IReadOnlyList<CatalogProduct>> ListProductsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CatalogProduct>>(
        [
            Product("opensuse-leap", "openSUSE Leap", "Stable release with a long support cycle, built from SUSE Linux Enterprise sources"),
            Product("opensuse-tumbleweed", "openSUSE Tumbleweed", "Rolling release with current software, tested before every snapshot"),
        ]);

    public async Task<IReadOnlyList<CatalogVariant>> ListVariantsAsync(string productId, CancellationToken cancellationToken)
    {
        switch (productId)
        {
            case "opensuse-tumbleweed":
                return [.. OpenSuseImages.Tumbleweed().Select((image, index) => Variant(productId, image, null, recommended: index == 0))];
            case "opensuse-leap":
                var releases = OpenSuseReleases.StableLeap(await _http.GetStringAsync(Distributions, cancellationToken).ConfigureAwait(false));
                return [.. releases.SelectMany((release, releaseIndex) => OpenSuseImages.Leap(release)
                    .Select((image, imageIndex) => Variant(productId, image, release.ToString(), recommended: releaseIndex == 0 && imageIndex == 0)))];
            default:
                throw new ArgumentException($"Unknown openSUSE product '{productId}'.", nameof(productId));
        }
    }

    public async Task<DownloadRequest> ResolveAsync(CatalogVariant variant, string? architecture, CancellationToken cancellationToken)
    {
        var arch = Architectures.Select(variant, architecture);
        var digest = variant.Property("digest");
        var file = variant.Property("file").Replace("{arch}", arch == Architectures.Arm64 ? "aarch64" : "x86_64", StringComparison.Ordinal);
        var image = new Uri(Site + variant.Property("directory") + file);

        var sums = await ChecksumSource.DetachedAsync(
            _http,
            new Uri($"{image.AbsoluteUri}.{digest}"),
            new Uri($"{image.AbsoluteUri}.{digest}.asc"),
            DistroKeys.OpenSuse,
            cancellationToken).ConfigureAwait(false);

        // "Current" and the stable Leap 16 names are links to a build-numbered file, so the signed file names that one;
        // each of these checksum files covers a single image, which is what ties it to the file asked for.
        var signed = sums.Entries.Count == 1 ? sums.Entries[0].Hash : sums.Pick(file);

        var mirrors = await MirrorsAsync(image, signed, cancellationToken).ConfigureAwait(false);
        return mirrors is null
            ? new DownloadRequest(image) { ExpectedHashes = [signed] }
            : new DownloadRequest(MirrorSources.Pick(mirrors.Mirrors))
            {
                ExpectedHashes = [signed],
                ExpectedSize = mirrors.Size,
                Pieces = mirrors.Pieces,
            };
    }

    /// <summary>The mirror list of this exact image, or null if it is missing, broken or describes a different file than the signed digest.</summary>
    private async Task<MetalinkFile?> MirrorsAsync(Uri image, FileHash signed, CancellationToken cancellationToken)
    {
        try
        {
            var text = await _http.TryGetStringAsync(new Uri(image.AbsoluteUri + ".meta4"), cancellationToken).ConfigureAwait(false);
            if (text is null)
            {
                return null;
            }

            var document = MetalinkDocument.Parse(text);
            var entry = document.Files.Count == 1 ? document.Files[0] : null;
            if (entry is null || !entry.Hashes.Any(h => h.Kind == signed.Kind && h.Hex == signed.Hex) || entry.Mirrors.Count == 0)
            {
                _logger.LogWarning("The mirror list of {Image} does not match the signed digest; using the redirector only", image);
                return null;
            }

            return entry;
        }
        catch (BootrixException ex) when (ex.Code is ErrorCode.CatalogUnavailable or ErrorCode.MetalinkInvalid)
        {
            _logger.LogWarning(ex, "The mirror list of {Image} is unavailable", image);
            return null;
        }
    }

    private CatalogVariant Variant(string productId, OpenSuseImage image, string? version, bool recommended) => new()
    {
        Id = image.Id,
        ProductId = productId,
        Provider = Id,
        Name = image.Name,
        Version = version,
        Architectures = image.Architectures,
        IsRecommended = recommended,
        Properties = VariantProperties.Signed(("directory", image.Directory), ("file", image.FileTemplate), ("digest", image.Digest)),
    };

    private CatalogProduct Product(string id, string name, string description) => new()
    {
        Id = id,
        Provider = Id,
        Family = CatalogFamily.Linux,
        Name = name,
        Description = description,
        Homepage = "https://www.opensuse.org/",
        License = "Open source",
    };
}
