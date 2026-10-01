// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Library;

public enum LibraryCleanupReason
{
    /// <summary>The same image is on the shared folder, so the local copy only takes room.</summary>
    ExistsOnShare,

    /// <summary>A newer version of the same product, architecture and language is kept instead.</summary>
    SupersededVersion,

    /// <summary>Not used for longer than the policy allows.</summary>
    NotUsedForLong,

    /// <summary>Removed, least recently used first, to get below the size limit.</summary>
    OverQuota,
}

/// <summary>What may be removed. Every limit is optional; with none set, nothing is.</summary>
public sealed record LibraryCleanupPolicy
{
    /// <summary>Upper limit for all local images together. The least recently used ones go first.</summary>
    public long? MaxTotalBytes { get; init; }

    /// <summary>Images that were last used longer ago than this.</summary>
    public TimeSpan? MaxIdle { get; init; }

    /// <summary>Keep this many versions of each product (per architecture and language); equal versions count as one.</summary>
    public int? KeepVersionsPerProduct { get; init; }

    /// <summary>Drop local copies of images that the shared folder has as well.</summary>
    public bool RemoveCopiesAlsoOnShare { get; init; }

    /// <summary>SHA-256 values that must stay, for example the ones that profiles refer to. They count towards the size but are never removed.</summary>
    public IReadOnlySet<string> Pinned { get; init; } = new HashSet<string>();
}

public sealed record LibraryCleanupItem(LibraryEntry Entry, LibraryCleanupReason Reason);

/// <summary>
/// What a cleanup would remove. Computing it deletes nothing, so it doubles as the preview; running it
/// (<see cref="ImageLibrary.CleanupAsync"/>) removes exactly these items.
/// </summary>
public sealed record LibraryCleanupPlan(IReadOnlyList<LibraryCleanupItem> Items, long BytesToFree, long BytesRemaining);

public sealed record LibraryCleanupFailure(LibraryEntry Entry, string Error);

public sealed record LibraryCleanupResult(IReadOnlyList<LibraryEntry> Removed, IReadOnlyList<LibraryCleanupFailure> Failed, long BytesFreed);
