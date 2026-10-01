// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Catalog.Distros.Common;
using Bootrix.Core.Net;

namespace Bootrix.Core.Catalog.Distros;

/// <summary>
/// Manjaro's official editions and community spins. The image list is Manjaro's own <c>file-info.json</c>; the digest
/// is the <c>.sha256</c> or <c>.sha512</c> beside the image. Manjaro signs only the ISO (detached .sig), so the digest
/// is as trustworthy as download.manjaro.org over HTTPS, and the variants say so.
/// </summary>
public sealed class ManjaroProvider : ICatalogProvider
{
    private static readonly Uri FileInfo = new("https://gitlab.manjaro.org/web/iso-info/-/raw/master/file-info.json");

    private static readonly Dictionary<string, string> EditionNames = new(StringComparer.Ordinal)
    {
        ["plasma"] = "KDE Plasma",
        ["xfce"] = "Xfce",
        ["gnome"] = "GNOME",
        ["cinnamon"] = "Cinnamon",
        ["i3"] = "i3",
        ["sway"] = "Sway",
    };

    private readonly DistroHttp _http;

    public ManjaroProvider(HttpClient http, TimeProvider? time = null)
    {
        _http = new DistroHttp(http, time);
    }

    public string Id => "manjaro";

    public Task<IReadOnlyList<CatalogProduct>> ListProductsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CatalogProduct>>(
        [
            new CatalogProduct
            {
                Id = "manjaro",
                Provider = Id,
                Family = CatalogFamily.Linux,
                Name = "Manjaro",
                Description = "Arch-based distribution with a graphical installer",
                Homepage = "https://manjaro.org/",
                License = "Open source",
            },
        ]);

    public async Task<IReadOnlyList<CatalogVariant>> ListVariantsAsync(string productId, CancellationToken cancellationToken)
    {
        if (productId != "manjaro")
        {
            throw new ArgumentException($"Unknown Manjaro product '{productId}'.", nameof(productId));
        }

        var editions = ManjaroReleases.Parse(await _http.GetStringAsync(FileInfo, cancellationToken).ConfigureAwait(false));

        var variants = new List<CatalogVariant>();
        foreach (var edition in editions)
        {
            var name = EditionNames.GetValueOrDefault(edition.Key, edition.Key);
            var community = edition.Group == "community";
            if (edition.Full is { } full)
            {
                variants.Add(Variant(edition, edition.Key, Title(name, community, minimal: false), full, recommended: edition is { Group: "official", Key: "plasma" }));
            }

            if (edition.Minimal is { } minimal)
            {
                variants.Add(Variant(edition, edition.Key + "-minimal", Title(name, community, minimal: true), minimal, recommended: false));
            }

            if (edition.Full is null && edition.Minimal is null && edition.CustomUrl is { } custom)
            {
                variants.Add(new CatalogVariant
                {
                    Id = edition.Key,
                    ProductId = "manjaro",
                    Provider = Id,
                    Name = Title(name, community, minimal: false),
                    Architectures = [Architectures.X64],
                    ManualUrl = custom.AbsoluteUri,
                    Properties = VariantProperties.Unsigned(("group", edition.Group)),
                });
            }
        }

        return variants;
    }

    public async Task<DownloadRequest> ResolveAsync(CatalogVariant variant, string? architecture, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(variant);
        if (variant.ManualUrl is not null)
        {
            throw new NotSupportedException($"'{variant.Name}' is published on its own site ({variant.ManualUrl}); download it there and choose the file.");
        }

        Architectures.Select(variant, architecture);
        var image = new Uri(variant.Property("image"));
        var file = variant.Property("file");

        var sums = await ChecksumSource.UnsignedAsync(_http, new Uri(variant.Property("checksum")), cancellationToken).ConfigureAwait(false);
        return new DownloadRequest(image) { ExpectedHashes = [sums.Pick(file)] };
    }

    private static string Title(string edition, bool community, bool minimal) => (community, minimal) switch
    {
        (false, false) => $"Manjaro {edition}",
        (false, true) => $"Manjaro {edition} (minimal)",
        (true, false) => $"Manjaro {edition} (community)",
        (true, true) => $"Manjaro {edition} (community, minimal)",
    };

    private CatalogVariant Variant(ManjaroEdition edition, string id, string name, ManjaroImage image, bool recommended)
    {
        var properties = new List<(string Key, string Value)>
        {
            ("image", image.Image.AbsoluteUri),
            ("checksum", image.Checksum.AbsoluteUri),
            ("file", image.FileName),
            ("group", edition.Group),
        };
        if (image.Signature is { } signature)
        {
            properties.Add(("signature", signature.AbsoluteUri));
        }

        return new CatalogVariant
        {
            Id = id,
            ProductId = "manjaro",
            Provider = Id,
            Name = name,
            Version = image.Version,
            Architectures = [Architectures.X64],
            ReleaseDate = image.Date,
            IsRecommended = recommended,
            Properties = VariantProperties.Unsigned([.. properties]),
        };
    }
}
