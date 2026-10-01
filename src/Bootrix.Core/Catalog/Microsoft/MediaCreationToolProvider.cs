// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Net;
using Bootrix.Core.Errors;
using Bootrix.Core.Net;
using Microsoft.Extensions.Logging;

namespace Bootrix.Core.Catalog.Microsoft;

/// <summary>
/// The fallback behind <see cref="MicrosoftIsoProvider"/>: the catalog that Microsoft's Media Creation Tool reads
/// (<c>products.cab</c>, reached through go.microsoft.com links that the tool itself uses). It lists the install
/// images as ESD files with their SHA-1; turning an ESD into an ISO is a later step, so every variant carries
/// <c>Properties["format"] = "esd"</c>.
/// </summary>
/// <remarks>
/// Trust: the catalog arrives over TLS. Its Authenticode signature is checked when an
/// <see cref="ICabSignatureVerifier"/> exists for the platform; the catalogs fetched on 2026-10-01 carried no
/// signature at all, so "not signed" and "not checked" only produce a warning, and a signature that is present but
/// wrong stops everything. The ESD files are served over plain HTTP (the delivery host's certificate does not cover
/// its name), which makes the SHA-1 from the catalog the only integrity check; a collision would need a second
/// preimage, which SHA-1 still resists.
/// </remarks>
public sealed class MediaCreationToolProvider : ICatalogProvider
{
    public const string ProviderId = "microsoft-mct";

    private const int MaxCabinetBytes = 8 * 1024 * 1024;

    private static readonly Product[] Products =
    [
        new("windows11-mct", "Windows 11 (Media Creation Tool, ESD)", new Uri("https://go.microsoft.com/fwlink/?LinkId=2156292"), "https://www.microsoft.com/software-download/windows11", null),
        new("windows10-mct", "Windows 10 (Media Creation Tool, ESD)", new Uri("https://go.microsoft.com/fwlink/?LinkId=841361"), "https://www.microsoft.com/software-download/windows10", WindowsReleases.Windows10EndOfSupport),
    ];

    private readonly HttpClient _http;
    private readonly ICabSignatureVerifier _verifier;
    private readonly ILogger<MediaCreationToolProvider> _logger;

    public MediaCreationToolProvider(HttpClient http, ILogger<MediaCreationToolProvider> logger)
        : this(http, new UnverifiedCabSignatureVerifier(), logger)
    {
    }

    public MediaCreationToolProvider(HttpClient http, ICabSignatureVerifier signatureVerifier, ILogger<MediaCreationToolProvider> logger)
    {
        _http = http;
        _verifier = signatureVerifier;
        _logger = logger;
    }

    public string Id => ProviderId;

    public Task<IReadOnlyList<CatalogProduct>> ListProductsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<CatalogProduct> products =
        [
            .. Products.Select(p => new CatalogProduct
            {
                Id = p.Id,
                Provider = ProviderId,
                Family = CatalogFamily.Windows,
                Name = p.Name,
                Description = "Install image in ESD format from the Media Creation Tool catalog. It is not an ISO and has to be converted before it can boot.",
                Homepage = p.Homepage,
                License = "proprietary",
            }),
        ];

        return Task.FromResult(products);
    }

    public async Task<IReadOnlyList<CatalogVariant>> ListVariantsAsync(string productId, CancellationToken cancellationToken)
    {
        var product = Products.FirstOrDefault(p => p.Id.Equals(productId, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"Unknown product '{productId}'.", nameof(productId));

        var (images, signature) = await LoadCatalogAsync(product, cancellationToken).ConfigureAwait(false);
        return BuildVariants(product, images, signature);
    }

    public Task<DownloadRequest> ResolveAsync(CatalogVariant variant, string? architecture, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(variant);

        var arch = ArchitectureChoice.Choose(variant, architecture);
        var prefix = $"esd.{arch}.";

        if (!variant.Properties.TryGetValue(prefix + "url", out var url)
            || !variant.Properties.TryGetValue(prefix + "sha1", out var sha1)
            || !variant.Properties.TryGetValue(prefix + "size", out var size))
        {
            throw new ArgumentException($"'{variant.Name}' was not produced by this provider.", nameof(variant));
        }

        var request = new DownloadRequest(new Uri(url))
        {
            ExpectedHashes = [new FileHash(HashKind.Sha1, sha1)],
            ExpectedSize = long.Parse(size, CultureInfo.InvariantCulture),
        };

        return Task.FromResult(request);
    }

    private static List<CatalogVariant> BuildVariants(Product product, IReadOnlyList<CatalogEsd> images, string signature)
    {
        var variants = new List<CatalogVariant>();

        foreach (var group in images.GroupBy(i => (i.Channel, i.LanguageCode)).OrderBy(g => g.Key.Channel).ThenBy(g => g.Key.LanguageCode, StringComparer.Ordinal))
        {
            // One image per architecture; if the catalog lists several builds, the newest wins.
            var files = group
                .GroupBy(i => i.Architecture)
                .Select(g => g.OrderByDescending(f => f.Built).ThenByDescending(f => f.FileName, StringComparer.Ordinal).First())
                .OrderBy(f => ArchitectureChoice.Rank(f.Architecture))
                .ToList();
            var preferred = files[0];
            var channel = group.Key.Channel.ToString().ToLowerInvariant();

            var properties = new Dictionary<string, string>
            {
                ["format"] = "esd",
                ["channel"] = channel,
                ["catalogSignature"] = signature,
                ["editions"] = string.Join(',', files.SelectMany(f => f.Editions).Distinct(StringComparer.Ordinal)),
            };

            foreach (var file in files)
            {
                var prefix = $"esd.{file.Architecture}.";
                properties[prefix + "url"] = file.Url.AbsoluteUri;
                properties[prefix + "sha1"] = file.Sha1.Hex;
                properties[prefix + "size"] = file.Size.ToString(CultureInfo.InvariantCulture);
                properties[prefix + "file"] = file.FileName;
            }

            variants.Add(new CatalogVariant
            {
                Id = $"{channel}/{group.Key.LanguageCode.ToLowerInvariant()}",
                ProductId = product.Id,
                Provider = ProviderId,
                Name = $"{ChannelName(group.Key.Channel)} - {preferred.Language}",
                Version = WindowsReleases.Describe(preferred.Release, preferred.Build),
                Language = preferred.LanguageCode,
                Architectures = ArchitectureChoice.Sort(files.Select(f => f.Architecture)),
                SizeBytes = preferred.Size,
                ReleaseDate = preferred.Built,
                EndOfSupport = product.EndOfSupport,
                Properties = properties,
            });
        }

        return variants;
    }

    private static string ChannelName(EsdChannel channel) => channel switch
    {
        EsdChannel.Consumer => "Consumer editions",
        EsdChannel.Business => "Business editions",
        EsdChannel.China => "China editions",
        _ => "Client editions",
    };

    private async Task<(IReadOnlyList<CatalogEsd> Images, string Signature)> LoadCatalogAsync(Product product, CancellationToken cancellationToken)
    {
        var response = await MicrosoftHttp.GetAsync(_http, product.CatalogUrl, referer: null, MaxCabinetBytes, cancellationToken).ConfigureAwait(false);

        if (response.Status != HttpStatusCode.OK)
        {
            throw MicrosoftHttp.ForStatus(product.CatalogUrl, response.Status, response.RetryAfter);
        }

        var signature = CheckSignature(product, response.Body);

        try
        {
            var cabinet = CabinetArchive.Parse(response.Body);
            var entry = cabinet.Entries.FirstOrDefault(e => e.Name.Equals("products.xml", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("the cabinet holds no products.xml");

            return (ProductsCatalog.Parse(cabinet.Extract(entry)), signature);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            throw MicrosoftHttp.Unavailable($"The catalog behind {product.CatalogUrl} cannot be read: {ex.Message}", ex);
        }
    }

    private string CheckSignature(Product product, byte[] cabinet)
    {
        var result = _verifier.Verify(cabinet);

        switch (result.Status)
        {
            case CabSignatureStatus.Invalid:
                throw new BootrixException(ErrorCode.SignatureInvalid, $"The signature of the catalog behind {product.CatalogUrl} does not verify.");
            case CabSignatureStatus.Valid:
                _logger.LogDebug("Catalog {Url} is signed by {Signer}", product.CatalogUrl, result.Signer);
                return "valid";
            case CabSignatureStatus.NotSigned:
                _logger.LogWarning("Catalog {Url} carries no signature; it is trusted because of the TLS connection only", product.CatalogUrl);
                return "not-signed";
            default:
                _logger.LogWarning("Signature of catalog {Url} was not checked on this platform", product.CatalogUrl);
                return "not-checked";
        }
    }

    private sealed record Product(string Id, string Name, Uri CatalogUrl, string Homepage, DateOnly? EndOfSupport);
}
