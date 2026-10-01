// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Library;

public enum LibraryLocation
{
    /// <summary>The library folder of this installation; images can be added and removed here.</summary>
    Local,

    /// <summary>A read-only folder, typically a network share that several workstations fill. Bootrix never writes to it.</summary>
    Shared,
}

/// <summary>What the library keeps about an image besides its content: where it came from and what it is.</summary>
public sealed record LibraryImageInfo
{
    /// <summary>Display name, usually the catalog's variant name.</summary>
    public string? Name { get; init; }

    public string? Version { get; init; }

    /// <summary>Product id in the catalog, such as "rescue-systemrescue"; ties the image to the variants an update check compares with.</summary>
    public string? CatalogId { get; init; }

    public string? VariantId { get; init; }

    /// <summary>"x64", "arm64", "x86"; null when not known or not relevant.</summary>
    public string? Architecture { get; init; }

    public string? Language { get; init; }

    /// <summary>Where it was downloaded from. Only the address up to the path is kept: query strings of download links often carry tokens.</summary>
    public string? Source { get; init; }

    /// <summary>The file name as the user or the vendor knows it; only its extension is used for the stored name.</summary>
    public string? FileName { get; init; }
}

public sealed record LibraryEntry
{
    public required string Sha256 { get; init; }

    public required long Size { get; init; }

    /// <summary>Full path of the image file.</summary>
    public required string Path { get; init; }

    public required LibraryLocation Location { get; init; }

    public required LibraryImageInfo Info { get; init; }

    public required DateTimeOffset DownloadedUtc { get; init; }

    public required DateTimeOffset LastUsedUtc { get; init; }
}
