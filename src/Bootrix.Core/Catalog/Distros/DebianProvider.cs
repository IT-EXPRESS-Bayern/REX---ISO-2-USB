// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Catalog.Distros.Common;
using Bootrix.Core.Net;

namespace Bootrix.Core.Catalog.Distros;

/// <summary>
/// The current stable Debian: the netinst installer for amd64 and arm64 and the live images with their desktops. The
/// checksum files in the image directories are signed with Debian's CD signing key.
/// </summary>
public sealed class DebianProvider : ICatalogProvider
{
    private const string Site = "https://cdimage.debian.org/";

    private static readonly (string Root, string? Location)[] Mirrors =
    [
        ("https://ftp.fau.de/", "DE"),
        ("https://mirror.dogado.de/", "DE"),
    ];

    private static readonly Dictionary<string, string> DesktopNames = new(StringComparer.Ordinal)
    {
        ["gnome"] = "GNOME",
        ["kde"] = "KDE Plasma",
        ["xfce"] = "Xfce",
        ["cinnamon"] = "Cinnamon",
        ["lxde"] = "LXDE",
        ["lxqt"] = "LXQt",
        ["mate"] = "MATE",
        ["standard"] = "without desktop",
        ["debian-junior"] = "Debian Junior",
    };

    private readonly DistroHttp _http;

    public DebianProvider(HttpClient http, TimeProvider? time = null)
    {
        _http = new DistroHttp(http, time);
    }

    public string Id => "debian";

    public Task<IReadOnlyList<CatalogProduct>> ListProductsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CatalogProduct>>(
        [
            new CatalogProduct
            {
                Id = "debian",
                Provider = Id,
                Family = CatalogFamily.Linux,
                Name = "Debian",
                Description = "The universal operating system; stable release",
                Homepage = "https://www.debian.org/",
                License = "Open source",
            },
        ]);

    public async Task<IReadOnlyList<CatalogVariant>> ListVariantsAsync(string productId, CancellationToken cancellationToken)
    {
        if (productId != "debian")
        {
            throw new ArgumentException($"Unknown Debian product '{productId}'.", nameof(productId));
        }

        // Listing reads the unverified copy; resolving a variant verifies the signature before a digest is used.
        var netinst = DebianImages.Netinst(await ChecksumSource.UnsignedAsync(_http, new Uri(Site + NetinstDirectory("amd64") + "SHA256SUMS"), cancellationToken).ConfigureAwait(false))
            .Where(i => i.Architecture == "amd64");
        var live = DebianImages.Live(await ChecksumSource.UnsignedAsync(_http, new Uri(Site + LiveDirectory("amd64") + "SHA256SUMS"), cancellationToken).ConfigureAwait(false));

        var variants = new List<CatalogVariant>();
        foreach (var image in netinst)
        {
            variants.Add(Variant(image, $"Debian {image.Version} netinst", [Architectures.X64, Architectures.Arm64], recommended: true));
        }

        foreach (var image in live)
        {
            var desktop = DesktopNames.GetValueOrDefault(image.Edition, image.Edition);
            variants.Add(Variant(image, $"Debian {image.Version} live ({desktop})", [Architectures.X64], recommended: false));
        }

        return variants;
    }

    public async Task<DownloadRequest> ResolveAsync(CatalogVariant variant, string? architecture, CancellationToken cancellationToken)
    {
        var arch = Architectures.Select(variant, architecture);
        var version = variant.Property("version");
        var edition = variant.Property("edition");

        var vendorArch = arch == Architectures.Arm64 ? "arm64" : "amd64";
        var (directory, file) = edition == "netinst"
            ? (NetinstDirectory(vendorArch), $"debian-{version}-{vendorArch}-netinst.iso")
            : (LiveDirectory(vendorArch), $"debian-live-{version}-{vendorArch}-{edition}.iso");

        var sums = await ChecksumSource.DetachedAsync(
            _http,
            new Uri(Site + directory + "SHA256SUMS"),
            new Uri(Site + directory + "SHA256SUMS.sign"),
            DistroKeys.Debian,
            cancellationToken).ConfigureAwait(false);

        return new DownloadRequest(MirrorSources.Build(new Uri(Site + directory + file), Mirrors, directory + file))
        {
            ExpectedHashes = [sums.Pick(file)],
        };
    }

    private CatalogVariant Variant(DebianImage image, string name, IReadOnlyList<string> architectures, bool recommended) => new()
    {
        Id = image.Edition == "netinst" ? $"{image.Version}/netinst" : $"{image.Version}/live-{image.Edition}",
        ProductId = "debian",
        Provider = Id,
        Name = name,
        Version = image.Version,
        Architectures = architectures,
        IsRecommended = recommended,
        Properties = VariantProperties.Signed(("version", image.Version), ("edition", image.Edition)),
    };

    private static string NetinstDirectory(string vendorArch) => $"debian-cd/current/{vendorArch}/iso-cd/";

    private static string LiveDirectory(string vendorArch) => $"debian-cd/current-live/{vendorArch}/iso-hybrid/";
}
