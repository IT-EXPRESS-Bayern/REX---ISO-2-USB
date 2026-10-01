// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Library;

internal static class LibraryCleanupPlanner
{
    /// <summary>
    /// The rules are applied from the least to the most drastic one, and an image keeps the first reason it was
    /// marked for: a copy that the share has anyway goes first, then old versions, then idle images, and only then
    /// images are dropped just for room.
    /// </summary>
    public static LibraryCleanupPlan Plan(
        IReadOnlyList<LibraryEntry> local,
        IReadOnlySet<string> sharedHashes,
        LibraryCleanupPolicy policy,
        DateTimeOffset now)
    {
        var removable = local.Where(e => !policy.Pinned.Contains(e.Sha256)).ToList();
        var marked = new Dictionary<string, LibraryCleanupReason>(StringComparer.Ordinal);

        if (policy.RemoveCopiesAlsoOnShare)
        {
            Mark(removable.Where(e => sharedHashes.Contains(e.Sha256)), LibraryCleanupReason.ExistsOnShare, marked);
        }

        if (policy.KeepVersionsPerProduct is { } keep)
        {
            Mark(Superseded(local, policy.Pinned, keep), LibraryCleanupReason.SupersededVersion, marked);
        }

        if (policy.MaxIdle is { } idle)
        {
            Mark(removable.Where(e => now - e.LastUsedUtc > idle), LibraryCleanupReason.NotUsedForLong, marked);
        }

        if (policy.MaxTotalBytes is { } limit)
        {
            var total = local.Where(e => !marked.ContainsKey(e.Sha256)).Sum(e => e.Size);
            var leastRecentlyUsed = removable
                .Where(e => !marked.ContainsKey(e.Sha256))
                .OrderBy(e => e.LastUsedUtc)
                .ThenBy(e => e.DownloadedUtc)
                .ThenBy(e => e.Sha256, StringComparer.Ordinal);

            foreach (var entry in leastRecentlyUsed)
            {
                if (total <= limit)
                {
                    break;
                }

                marked[entry.Sha256] = LibraryCleanupReason.OverQuota;
                total -= entry.Size;
            }
        }

        var items = local.Where(e => marked.ContainsKey(e.Sha256)).Select(e => new LibraryCleanupItem(e, marked[e.Sha256])).ToList();
        var toFree = items.Sum(i => i.Entry.Size);
        return new LibraryCleanupPlan(items, toFree, local.Sum(e => e.Size) - toFree);
    }

    private static void Mark(IEnumerable<LibraryEntry> entries, LibraryCleanupReason reason, Dictionary<string, LibraryCleanupReason> marked)
    {
        foreach (var entry in entries)
        {
            marked.TryAdd(entry.Sha256, reason);
        }
    }

    /// <summary>
    /// Images of products with a version older than the newest <paramref name="keep"/> ones. Only images that name
    /// their product and version take part, and pinned images are never superseded.
    /// </summary>
    private static IEnumerable<LibraryEntry> Superseded(IReadOnlyList<LibraryEntry> local, IReadOnlySet<string> pinned, int keep)
    {
        var groups = local
            .Where(e => e.Info.CatalogId is not null && e.Info.Version is not null)
            .GroupBy(e => (e.Info.CatalogId, e.Info.Architecture, e.Info.Language));

        foreach (var group in groups)
        {
            var kept = group.Select(e => e.Info.Version).Distinct().OrderByDescending(v => v, VersionOrder.Instance).Take(keep).ToHashSet();

            foreach (var entry in group.Where(e => !kept.Contains(e.Info.Version) && !pinned.Contains(e.Sha256)))
            {
                yield return entry;
            }
        }
    }
}
