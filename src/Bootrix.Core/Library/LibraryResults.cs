// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Library;

public enum LibraryAddMode
{
    /// <summary>The source file stays where it is.</summary>
    Copy,

    /// <summary>The source file is moved into the library (a rename when both are on one volume) and is gone afterwards.</summary>
    Move,
}

/// <param name="AlreadyPresent">The library already had this content; nothing was stored a second time, only the metadata was refreshed.</param>
public sealed record LibraryAddResult(LibraryEntry Entry, bool AlreadyPresent);

/// <summary>The same content in more than one place, such as a local copy of an image the shared folder has as well.</summary>
public sealed record LibraryDuplicate(string Sha256, IReadOnlyList<LibraryEntry> Entries);

public enum LibraryVerifyOutcome
{
    Ok,
    Missing,

    /// <summary>The file has another length than recorded.</summary>
    SizeChanged,

    /// <summary>Same length, different content: bit rot, a bad disk, or a file that was replaced.</summary>
    ContentChanged,
}

/// <param name="Removed">Files that were deleted: metadata of images that are gone or cut off, images that no longer have their recorded size, and abandoned temporary files.</param>
public sealed record LibraryRepairResult(IReadOnlyList<string> Removed);
