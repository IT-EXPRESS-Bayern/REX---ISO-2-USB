// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Net;

/// <summary>What is known about one source after it was probed; replaced as a whole when the link is renewed.</summary>
internal sealed record SourceLink(Uri Origin, Uri Resolved, string? ETag, DateTimeOffset? LastModified, int Generation);

internal sealed class DownloadSource(int index, MirrorSource spec, SourceLink link)
{
    private SourceLink _link = link;

    public int Index { get; } = index;

    public MirrorSource Spec { get; } = spec;

    public SourceLink Link
    {
        get => Volatile.Read(ref _link);
        set => Volatile.Write(ref _link, value);
    }

    /// <summary>Serialises link renewal so that several workers hitting 403 at once cause a single refresh.</summary>
    public SemaphoreSlim RefreshGate { get; } = new(1, 1);

    public int LinkRefreshes { get; set; }

    internal int Active { get; set; }

    internal int Leases { get; set; }

    internal bool Banned { get; set; }

    internal int Capacity => Spec.MaxConnections > 0 ? Spec.MaxConnections : int.MaxValue;
}

/// <summary>
/// Spreads segments over the sources: the least busy one gets the next segment, ties go to the one used
/// least so far (round robin) and then to the better priority. A source's connection limit is respected.
/// </summary>
internal sealed class SourcePool(IReadOnlyList<DownloadSource> sources)
{
    private readonly object _gate = new();

    public IReadOnlyList<DownloadSource> All { get; } = sources;

    public DownloadSource? Lease()
    {
        lock (_gate)
        {
            DownloadSource? best = null;
            foreach (var source in All)
            {
                if (source.Banned || source.Active >= source.Capacity)
                {
                    continue;
                }

                if (best is null || IsBetter(source, best))
                {
                    best = source;
                }
            }

            if (best is not null)
            {
                best.Active++;
                best.Leases++;
            }

            return best;
        }
    }

    public void Return(DownloadSource source)
    {
        lock (_gate)
        {
            source.Active--;
        }
    }

    /// <summary>
    /// Stops using a source and returns true, also if it was already dropped. The last usable source is kept
    /// and false is returned, so the real error surfaces instead of an empty pool.
    /// </summary>
    public bool Ban(DownloadSource source)
    {
        lock (_gate)
        {
            if (source.Banned)
            {
                return true;
            }

            if (All.Count(s => !s.Banned) <= 1)
            {
                return false;
            }

            source.Banned = true;
            return true;
        }
    }

    /// <summary>How many connections the usable sources accept in total, capped at <paramref name="limit"/>.</summary>
    public int Capacity(int limit)
    {
        lock (_gate)
        {
            long total = 0;
            foreach (var source in All.Where(s => !s.Banned))
            {
                total += source.Capacity;
                if (total >= limit)
                {
                    return limit;
                }
            }

            return (int)total;
        }
    }

    private static bool IsBetter(DownloadSource candidate, DownloadSource current)
    {
        if (candidate.Active != current.Active)
        {
            return candidate.Active < current.Active;
        }

        if (candidate.Leases != current.Leases)
        {
            return candidate.Leases < current.Leases;
        }

        return candidate.Spec.Priority < current.Spec.Priority;
    }
}
