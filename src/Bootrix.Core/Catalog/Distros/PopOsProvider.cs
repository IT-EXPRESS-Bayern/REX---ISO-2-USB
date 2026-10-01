// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Catalog.Distros.Common;
using Bootrix.Core.Errors;
using Bootrix.Core.Net;

namespace Bootrix.Core.Catalog.Distros;

/// <summary>
/// Pop!_OS from System76. Its build API names the current ISO of a release and graphics channel together with size
/// and SHA-256; System76 publishes no signature for it, so the digest is only as trustworthy as the API over HTTPS.
/// The API has no list of releases, so the LTS releases are asked for one by one and the ones it does not know are
/// skipped; a new LTS shows up without an update of Bootrix.
/// </summary>
public sealed class PopOsProvider : ICatalogProvider
{
    private static readonly string[] Releases = ["26.04", "24.04", "22.04"];

    private static readonly (string Channel, string Name)[] Channels =
    [
        ("intel", "Intel/AMD graphics"),
        ("nvidia", "NVIDIA graphics"),
    ];

    private readonly DistroHttp _http;

    public PopOsProvider(HttpClient http, TimeProvider? time = null)
    {
        _http = new DistroHttp(http, time);
    }

    public string Id => "popos";

    public Task<IReadOnlyList<CatalogProduct>> ListProductsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CatalogProduct>>(
        [
            new CatalogProduct
            {
                Id = "popos",
                Provider = Id,
                Family = CatalogFamily.Linux,
                Name = "Pop!_OS",
                Description = "Ubuntu-based distribution from System76 with drivers for NVIDIA graphics",
                Homepage = "https://pop.system76.com/",
                License = "Open source",
            },
        ]);

    public async Task<IReadOnlyList<CatalogVariant>> ListVariantsAsync(string productId, CancellationToken cancellationToken)
    {
        if (productId != "popos")
        {
            throw new ArgumentException($"Unknown Pop!_OS product '{productId}'.", nameof(productId));
        }

        var requests = Releases.SelectMany(release => Channels.Select(channel => (Release: release, channel.Channel, channel.Name))).ToList();
        var builds = await Task.WhenAll(requests.Select(r => BuildAsync(r.Release, r.Channel, cancellationToken))).ConfigureAwait(false);

        var variants = new List<CatalogVariant>();
        var recommendationGiven = false;
        for (var i = 0; i < requests.Count; i++)
        {
            if (builds[i] is not { } build)
            {
                continue;
            }

            // System76 recommends the plain (Intel/AMD) image of its newest release.
            var recommended = !recommendationGiven && requests[i].Channel == "intel";
            recommendationGiven |= recommended;

            variants.Add(new CatalogVariant
            {
                Id = $"{requests[i].Release}/{requests[i].Channel}",
                ProductId = productId,
                Provider = Id,
                Name = $"Pop!_OS {requests[i].Release} LTS ({requests[i].Name})",
                Version = requests[i].Release,
                Architectures = [Architectures.X64],
                SizeBytes = build.Size,
                IsRecommended = recommended,
                Properties = VariantProperties.Unsigned(("release", requests[i].Release), ("channel", requests[i].Channel)),
            });
        }

        return variants;
    }

    public async Task<DownloadRequest> ResolveAsync(CatalogVariant variant, string? architecture, CancellationToken cancellationToken)
    {
        Architectures.Select(variant, architecture);
        var release = variant.Property("release");
        var channel = variant.Property("channel");

        var build = await BuildAsync(release, channel, cancellationToken).ConfigureAwait(false)
            ?? throw new BootrixException(ErrorCode.CatalogUnavailable, $"Pop!_OS {release} ({channel}) is no longer offered by the build API");

        return new DownloadRequest(build.Url) { ExpectedHashes = [build.Sha256], ExpectedSize = build.Size };
    }

    /// <summary>The build of a release and channel, or null if System76 does not offer that release (the API answers 404).</summary>
    private async Task<PopOsBuild?> BuildAsync(string release, string channel, CancellationToken cancellationToken)
    {
        var json = await _http.TryGetStringAsync(new Uri($"https://api.pop-os.org/builds/{release}/{channel}"), cancellationToken).ConfigureAwait(false);
        return json is null ? null : PopOsBuild.Parse(json);
    }
}
