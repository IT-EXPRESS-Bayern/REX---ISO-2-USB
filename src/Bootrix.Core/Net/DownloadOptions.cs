// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Frozen;

namespace Bootrix.Core.Net;

public sealed record DownloadOptions
{
    /// <summary>Upper bound for parallel connections. Servers that cap a client lower than this simply queue the rest.</summary>
    public int MaxSegments { get; init; } = 8;

    /// <summary>A segment is never split below this size; opening a connection costs more than it gains.</summary>
    public long MinSegmentSize { get; init; } = 4L * 1024 * 1024;

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Time to the first response header.</summary>
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>A body read that delivers nothing for this long counts as a broken connection.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public string UserAgent { get; init; } = $"{AppInfo.Name}/{AppInfo.Version}";

    public IReadOnlyDictionary<string, string> Headers { get; init; } = FrozenDictionary<string, string>.Empty;

    /// <summary>Consecutive failures a segment may take before the download is given up.</summary>
    public int MaxRetries { get; init; } = 6;

    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan RetryMaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Longer <c>Retry-After</c> values from the server are cut to this.</summary>
    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>How often an expired link may be renewed before 403/410 is treated as final.</summary>
    public int MaxLinkRefreshes { get; init; } = 3;

    public int MaxRedirects { get; init; } = 10;

    public int BufferSize { get; init; } = 128 * 1024;

    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How often the resume file is rewritten while downloading.</summary>
    public TimeSpan StateSaveInterval { get; init; } = TimeSpan.FromSeconds(3);

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxSegments, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxSegments, 64);
        ArgumentOutOfRangeException.ThrowIfLessThan(MinSegmentSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxRetries, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxLinkRefreshes, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxRedirects, 0);
        ArgumentOutOfRangeException.ThrowIfLessThan(BufferSize, 4096);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(StallTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ResponseTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ConnectTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ProgressInterval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(StateSaveInterval, TimeSpan.Zero);
    }
}
