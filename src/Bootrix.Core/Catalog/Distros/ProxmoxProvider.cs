// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Catalog.Distros.Common;
using Bootrix.Core.Net;

namespace Bootrix.Core.Catalog.Distros;

/// <summary>
/// The Proxmox installers. One <c>SHA256SUMS</c> on enterprise.proxmox.com covers every ISO of all products; it is not
/// signed, so the digest is only as trustworthy as that server over HTTPS. Proxmox says to write its hybrid ISOs to
/// the stick as they are (DD mode), which the media planner decides from the image itself.
/// </summary>
public sealed class ProxmoxProvider : ICatalogProvider
{
    private const string Site = "https://enterprise.proxmox.com/iso/";

    private static readonly Product[] Products =
    [
        new("proxmox-ve", "Proxmox VE", "Virtualization platform for virtual machines and containers"),
        new("proxmox-backup-server", "Proxmox Backup Server", "Backup server with deduplication for virtual and physical machines"),
        new("proxmox-mail-gateway", "Proxmox Mail Gateway", "Mail proxy with spam and virus filtering"),
        new("proxmox-datacenter-manager", "Proxmox Datacenter Manager", "Central view over several Proxmox installations"),
    ];

    private readonly DistroHttp _http;

    public ProxmoxProvider(HttpClient http, TimeProvider? time = null)
    {
        _http = new DistroHttp(http, time);
    }

    public string Id => "proxmox";

    public Task<IReadOnlyList<CatalogProduct>> ListProductsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CatalogProduct>>([.. Products.Select(p => new CatalogProduct
        {
            Id = p.Id,
            Provider = Id,
            Family = CatalogFamily.Linux,
            Name = p.Name,
            Description = p.Description,
            Homepage = "https://www.proxmox.com/",
            License = "AGPL-3.0",
        })]);

    public async Task<IReadOnlyList<CatalogVariant>> ListVariantsAsync(string productId, CancellationToken cancellationToken)
    {
        var product = Products.FirstOrDefault(p => p.Id == productId)
            ?? throw new ArgumentException($"Unknown Proxmox product '{productId}'.", nameof(productId));

        var images = ProxmoxImages.Parse(await ChecksumSource.UnsignedAsync(_http, new Uri(Site + "SHA256SUMS"), cancellationToken).ConfigureAwait(false))
            .Where(i => i.Product == productId)
            .ToList();
        var newest = images.Count == 0 ? null : images[0].Version;

        return [.. images
            .GroupBy(i => i.Version)
            .Select(g => new CatalogVariant
            {
                Id = g.Key,
                ProductId = productId,
                Provider = Id,
                Name = $"{product.Name} {g.Key}",
                Version = g.Key,
                Architectures = [.. g.Select(i => i.Architecture).Distinct().OrderByDescending(a => a == Architectures.X64)],
                IsRecommended = g.Key == newest,
                Properties = VariantProperties.Unsigned(("version", g.Key)),
            })];
    }

    public async Task<DownloadRequest> ResolveAsync(CatalogVariant variant, string? architecture, CancellationToken cancellationToken)
    {
        var arch = Architectures.Select(variant, architecture);
        var file = $"{variant.ProductId}_{variant.Property("version")}{(arch == Architectures.Arm64 ? "-arm64" : string.Empty)}.iso";

        var sums = await ChecksumSource.UnsignedAsync(_http, new Uri(Site + "SHA256SUMS"), cancellationToken).ConfigureAwait(false);
        return new DownloadRequest(new Uri(Site + file)) { ExpectedHashes = [sums.Pick(file)] };
    }

    private sealed record Product(string Id, string Name, string Description);
}
