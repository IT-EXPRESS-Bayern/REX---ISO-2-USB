// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Library;

/// <summary>
/// The sidecar file <c>&lt;sha256&gt;.json</c> next to an image. Fields are nullable because the file is read from
/// disk or from a share and is checked by <see cref="LibraryScanner"/> before any of it is used.
/// </summary>
internal sealed record LibraryMetadata
{
    public const int CurrentSchema = 1;

    public int Schema { get; init; }

    public string? Sha256 { get; init; }

    /// <summary>Name of the image file in the same folder, always "&lt;sha256&gt;.&lt;extension&gt;".</summary>
    public string? File { get; init; }

    public long Size { get; init; }

    public string? Name { get; init; }

    public string? Version { get; init; }

    public string? CatalogId { get; init; }

    public string? VariantId { get; init; }

    public string? Architecture { get; init; }

    public string? Language { get; init; }

    public string? Source { get; init; }

    public DateTimeOffset? DownloadedUtc { get; init; }

    public DateTimeOffset? LastUsedUtc { get; init; }

    public static LibraryMetadata For(string sha256, string file, long size, LibraryImageInfo info, DateTimeOffset downloaded, DateTimeOffset lastUsed) => new()
    {
        Schema = CurrentSchema,
        Sha256 = sha256,
        File = file,
        Size = size,
        Name = LibraryFiles.CleanText(info.Name),
        Version = LibraryFiles.CleanText(info.Version),
        CatalogId = LibraryFiles.CleanText(info.CatalogId),
        VariantId = LibraryFiles.CleanText(info.VariantId),
        Architecture = LibraryFiles.CleanText(info.Architecture),
        Language = LibraryFiles.CleanText(info.Language),
        Source = LibraryFiles.CleanSource(info.Source),
        DownloadedUtc = downloaded,
        LastUsedUtc = lastUsed,
    };

    /// <summary>The descriptive part, cleaned the same way whether it was just supplied or read back from a share.</summary>
    public LibraryImageInfo ToInfo() => new()
    {
        Name = LibraryFiles.CleanText(Name),
        Version = LibraryFiles.CleanText(Version),
        CatalogId = LibraryFiles.CleanText(CatalogId),
        VariantId = LibraryFiles.CleanText(VariantId),
        Architecture = LibraryFiles.CleanText(Architecture),
        Language = LibraryFiles.CleanText(Language),
        Source = LibraryFiles.CleanSource(Source),
    };
}
