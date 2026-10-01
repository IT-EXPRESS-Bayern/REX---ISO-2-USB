// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;
using Bootrix.Core.Catalog.Distros.Common;
using Bootrix.Core.Net;

namespace Bootrix.Core.Catalog.Distros;

/// <summary>
/// Clonezilla Live, the stable branch. The project's own mirror at NCHC (Taiwan) carries the signed SHA256SUMS; the
/// signing key is the one the project publishes at clonezilla.org. SourceForge serves the same files as a second source.
/// </summary>
public sealed partial class ClonezillaProvider : ICatalogProvider
{
    private const string Mirror = "https://free.nchc.org.tw/clonezilla-live/stable/";
    private const string SourceForge = "https://downloads.sourceforge.net/project/clonezilla/clonezilla_live_stable/";

    private readonly DistroHttp _http;

    public ClonezillaProvider(HttpClient http, TimeProvider? time = null)
    {
        _http = new DistroHttp(http, time);
    }

    public string Id => "clonezilla";

    public Task<IReadOnlyList<CatalogProduct>> ListProductsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CatalogProduct>>(
        [
            new CatalogProduct
            {
                Id = "clonezilla",
                Provider = Id,
                Family = CatalogFamily.Rescue,
                Name = "Clonezilla Live",
                Description = "Disk and partition imaging and cloning",
                Homepage = "https://clonezilla.org/",
                License = "GPL-2.0",
            },
        ]);

    public async Task<IReadOnlyList<CatalogVariant>> ListVariantsAsync(string productId, CancellationToken cancellationToken)
    {
        if (productId != "clonezilla")
        {
            throw new ArgumentException($"Unknown Clonezilla product '{productId}'.", nameof(productId));
        }

        var sums = await ChecksumSource.UnsignedAsync(_http, new Uri(Mirror + "SHA256SUMS"), cancellationToken).ConfigureAwait(false);

        // The zip next to the ISO holds the same files for a manual USB setup and is not offered.
        return [.. sums.Entries
            .Select(e => ImageName().Match(e.FileName ?? string.Empty))
            .Where(m => m.Success)
            .Select((m, index) => new CatalogVariant
            {
                Id = m.Groups["version"].Value,
                ProductId = productId,
                Provider = Id,
                Name = $"Clonezilla Live {m.Groups["version"].Value}",
                Version = m.Groups["version"].Value,
                Architectures = [Architectures.X64],
                IsRecommended = index == 0,
                Properties = VariantProperties.Signed(("version", m.Groups["version"].Value)),
            })];
    }

    public async Task<DownloadRequest> ResolveAsync(CatalogVariant variant, string? architecture, CancellationToken cancellationToken)
    {
        Architectures.Select(variant, architecture);
        var version = variant.Property("version");
        var file = $"clonezilla-live-{version}-amd64.iso";

        var sums = await ChecksumSource.DetachedAsync(
            _http,
            new Uri(Mirror + "SHA256SUMS"),
            new Uri(Mirror + "SHA256SUMS.gpg"),
            DistroKeys.Clonezilla,
            cancellationToken).ConfigureAwait(false);

        return new DownloadRequest([new MirrorSource(new Uri(Mirror + file), 1), new MirrorSource(new Uri($"{SourceForge}{version}/{file}"), 2)])
        {
            ExpectedHashes = [sums.Pick(file)],
        };
    }

    [GeneratedRegex(@"^clonezilla-live-(?<version>\d+(?:\.\d+)*-\d+)-amd64\.iso$")]
    private static partial Regex ImageName();
}
