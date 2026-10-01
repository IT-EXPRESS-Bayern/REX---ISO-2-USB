// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Catalog;

namespace Bootrix.Core.Library;

[Flags]
public enum LibraryUpdateStatus
{
    UpToDate = 0,

    /// <summary>The catalog offers a newer version of the same product.</summary>
    NewerAvailable = 1,

    /// <summary>The catalog says that support for the stored version has ended.</summary>
    SupportEnded = 2,

    /// <summary>No statement is possible: the image has no product or version, the catalog does not know the product, or it could not be reached.</summary>
    Unchecked = 4,
}

/// <param name="Newer">The newest variant of the product that fits the image's architecture and language, if it is newer than the image.</param>
/// <param name="SupportEndsOn">When support for the stored version ends or ended, if the catalog knows.</param>
public sealed record LibraryUpdateInfo(LibraryEntry Entry, LibraryUpdateStatus Status, CatalogVariant? Newer, DateOnly? SupportEndsOn);

/// <summary>Compares library images with the variants the catalog offers today.</summary>
public interface ILibraryUpdateCheck
{
    /// <summary>One result per entry, in the order given. Catalog failures make entries <see cref="LibraryUpdateStatus.Unchecked"/>; they do not throw.</summary>
    Task<IReadOnlyList<LibraryUpdateInfo>> CheckAsync(IReadOnlyList<LibraryEntry> entries, CancellationToken cancellationToken);
}
