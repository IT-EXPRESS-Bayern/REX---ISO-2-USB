// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Tests.Net.Support;

/// <summary>What the test server should do to one response instead of (or while) serving it normally.</summary>
internal sealed record Fault
{
    public static readonly Fault None = new();

    /// <summary>Answer with this status and an empty body.</summary>
    public int? StatusCode { get; init; }

    public int? RetryAfterSeconds { get; init; }

    /// <summary>Send this many body bytes, then drop the connection.</summary>
    public long? AbortAfterBytes { get; init; }

    /// <summary>Serve the body at this rate.</summary>
    public long? BytesPerSecond { get; init; }

    /// <summary>Flip one bit at the start of every 16 KiB chunk of the body.</summary>
    public bool Corrupt { get; init; }

    public static Fault Status(int code, int? retryAfterSeconds = null) =>
        new() { StatusCode = code, RetryAfterSeconds = retryAfterSeconds };

    public static Fault AbortAfter(long bytes) => new() { AbortAfterBytes = bytes };

    public static Fault Throttle(long bytesPerSecond) => new() { BytesPerSecond = bytesPerSecond };

    public static Fault Garble() => new() { Corrupt = true };
}

/// <param name="Index">Running number of the request on this server, starting at 1.</param>
/// <param name="RangeStart">First requested byte, or null if the request had no Range header.</param>
internal sealed record RequestInfo(int Index, string Path, long? RangeStart, long? RangeEnd, bool HasIfRange);

internal sealed record RequestRecord(int Index, string Path, long? RangeStart, long? RangeEnd, bool HasIfRange, int Status, long BytesSent);
