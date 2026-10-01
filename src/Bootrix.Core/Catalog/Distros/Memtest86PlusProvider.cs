// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Catalog.Distros.Common;
using Bootrix.Core.Net;

namespace Bootrix.Core.Catalog.Distros;

/// <summary>
/// Memtest86+ from memtest.org. The front page links the current release; <c>sha256sum.txt</c> in the release
/// directory has the digests. Nothing is signed, so the variants say the digest is only as trustworthy as the site over
/// HTTPS. The ISO comes inside a zip; the variant's <see cref="DistroProperties.Archive"/> and
/// <see cref="DistroProperties.ArchiveEntry"/> tell the caller what to unpack.
/// </summary>
public sealed class Memtest86PlusProvider : ICatalogProvider
{
    private static readonly Uri FrontPage = new("https://www.memtest.org/");

    private static readonly Dictionary<string, string> KindNames = new(StringComparer.Ordinal)
    {
        ["x86_64"] = "64-bit",
        ["x86_64-grub"] = "64-bit, with GRUB",
        ["i586"] = "32-bit",
    };

    private readonly DistroHttp _http;

    public Memtest86PlusProvider(HttpClient http, TimeProvider? time = null)
    {
        _http = new DistroHttp(http, time);
    }

    public string Id => "memtest86plus";

    public Task<IReadOnlyList<CatalogProduct>> ListProductsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CatalogProduct>>(
        [
            new CatalogProduct
            {
                Id = "memtest86plus",
                Provider = Id,
                Family = CatalogFamily.Utility,
                Name = "Memtest86+",
                Description = "Memory test that boots without an operating system",
                Homepage = "https://www.memtest.org/",
                License = "GPL-2.0",
            },
        ]);

    public async Task<IReadOnlyList<CatalogVariant>> ListVariantsAsync(string productId, CancellationToken cancellationToken)
    {
        if (productId != "memtest86plus")
        {
            throw new ArgumentException($"Unknown Memtest86+ product '{productId}'.", nameof(productId));
        }

        var downloads = MemtestDownloads.Parse(await _http.GetStringAsync(FrontPage, cancellationToken).ConfigureAwait(false));
        return [.. downloads.Select((download, index) => new CatalogVariant
        {
            Id = $"{download.Version}/{download.Kind}",
            ProductId = productId,
            Provider = Id,
            Name = $"Memtest86+ {download.Version} ({KindNames[download.Kind]})",
            Version = download.Version,
            Architectures = [download.Architecture],
            IsRecommended = index == 0,
            Properties = VariantProperties.Unsigned(
                ("version", download.Version),
                ("file", download.FileName),
                (DistroProperties.Archive, "zip"),
                (DistroProperties.ArchiveEntry, download.ImageInArchive)),
        })];
    }

    public async Task<DownloadRequest> ResolveAsync(CatalogVariant variant, string? architecture, CancellationToken cancellationToken)
    {
        Architectures.Select(variant, architecture);
        var directory = MemtestDownloads.DirectoryFor(variant.Property("version"));
        var file = variant.Property("file");

        // The digest list names files with their directory ("v8.10/mt86plus_8.10_x86_64.iso.zip").
        var sums = await ChecksumSource.UnsignedAsync(_http, new Uri(directory, "sha256sum.txt"), cancellationToken).ConfigureAwait(false);
        return new DownloadRequest(new Uri(directory, file)) { ExpectedHashes = [sums.Pick(file)] };
    }
}
