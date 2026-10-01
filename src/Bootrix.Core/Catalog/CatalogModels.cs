// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Net;

namespace Bootrix.Core.Catalog;

public enum CatalogFamily
{
    Windows,
    Linux,
    Bsd,
    Dos,
    Rescue,
    Utility,
}

/// <summary>What the user picks first: "Windows 11", "Ubuntu", "SystemRescue".</summary>
public sealed record CatalogProduct
{
    /// <summary>Stable id, unique across providers, e.g. "windows11" or "ubuntu".</summary>
    public required string Id { get; init; }

    public required string Provider { get; init; }

    public required CatalogFamily Family { get; init; }

    public required string Name { get; init; }

    /// <summary>One line in plain words; proper names and descriptions of third-party products are data, not UI texts.</summary>
    public string? Description { get; init; }

    public string? Homepage { get; init; }

    /// <summary>Short licence tag such as "GPL-2.0" or "proprietary", shown next to the download.</summary>
    public string? License { get; init; }
}

/// <summary>One concrete choice below a product: a release, edition or language.</summary>
public sealed record CatalogVariant
{
    /// <summary>Stable id inside the product, e.g. "24.04.3/desktop" or "25H2/de-de".</summary>
    public required string Id { get; init; }

    public required string ProductId { get; init; }

    public required string Provider { get; init; }

    public required string Name { get; init; }

    public string? Version { get; init; }

    public string? Language { get; init; }

    /// <summary>Architectures offered for this variant, as "x64", "arm64", "x86" or "i386"; empty when the image is architecture neutral.</summary>
    public IReadOnlyList<string> Architectures { get; init; } = [];

    public long? SizeBytes { get; init; }

    public DateOnly? ReleaseDate { get; init; }

    public DateOnly? EndOfSupport { get; init; }

    /// <summary>Long-term-support release or the currently recommended one; sorted first.</summary>
    public bool IsRecommended { get; init; }

    /// <summary>
    /// The file cannot be fetched automatically (the vendor only offers a web page or a login). The user is sent to
    /// <see cref="ManualUrl"/> and picks the file from disk afterwards.
    /// </summary>
    public string? ManualUrl { get; init; }

    /// <summary>Provider-private values needed to resolve the download later; never shown to the user.</summary>
    public IReadOnlyDictionary<string, string> Properties { get; init; } = new Dictionary<string, string>();
}

/// <summary>Why a provider could not deliver, so one broken vendor site does not hide everything else.</summary>
public sealed record ProviderFailure(string Provider, Exception Error);

public sealed record CatalogListing<T>(IReadOnlyList<T> Items, IReadOnlyList<ProviderFailure> Failures);
