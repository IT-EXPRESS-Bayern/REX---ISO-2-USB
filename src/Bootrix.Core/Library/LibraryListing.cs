// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Library;

public enum LibraryProblemKind
{
    /// <summary>The library folder cannot be read, for example because the network share is offline.</summary>
    FolderUnavailable,

    /// <summary>The metadata file cannot be read or is not valid JSON.</summary>
    UnreadableMetadata,

    /// <summary>The metadata is readable but contradicts itself or the rules (wrong hash, unsafe file name, bad schema).</summary>
    InvalidMetadata,

    /// <summary>The metadata names an image that is not there any more.</summary>
    MissingImage,

    /// <summary>The image has another size than recorded, so it was cut off or replaced.</summary>
    SizeMismatch,

    /// <summary>The image is a link; links are not followed because they could point anywhere.</summary>
    LinkNotFollowed,

    /// <summary>An image file without metadata. Not removed automatically: it may be one that is just being added.</summary>
    Unindexed,
}

/// <param name="Path">The file the problem was found at.</param>
public sealed record LibraryProblem(LibraryLocation Location, string Path, LibraryProblemKind Kind, string Detail);

/// <summary>
/// What the library folders hold after checking them. The index is not trusted blindly: an entry only appears if its
/// metadata is valid and its image is present with the recorded size. Everything else is listed as a problem.
/// </summary>
public sealed record LibraryListing(IReadOnlyList<LibraryEntry> Entries, IReadOnlyList<LibraryProblem> Problems);
