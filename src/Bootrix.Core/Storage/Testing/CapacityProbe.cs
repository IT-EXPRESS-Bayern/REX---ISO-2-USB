// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Diagnostics;

namespace Bootrix.Core.Storage.Testing;

public sealed record CapacityProbeResult(
    bool IsGenuine,
    long ClaimedBytes,
    long? EstimatedRealBytes,
    long? FirstFailingOffset,
    int ProbePoints,
    int FailedPoints,
    double WriteMegabytesPerSecond,
    double ReadMegabytesPerSecond);

/// <summary>
/// Quick check whether a stick really has the capacity it claims. Counterfeit sticks usually wrap
/// around (a write beyond the real end lands at the start) or swallow data. Random blocks are
/// written all over the claimed range with a self-describing tag; when a stick wraps, some blocks
/// overwrite others and the tags that come back give away the real size. This is destructive, so
/// it belongs before a write, never after.
/// </summary>
public static class CapacityProbe
{
    private const int Magic = 0x42545850; // "BTXP"
    private const int HeaderBytes = 16;

    public static async Task<CapacityProbeResult> RunAsync(
        IBlockDevice device,
        int? probePoints = null,
        int seed = 20240601,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var block = Math.Max(4096, device.SectorSize);
        var blocks = device.Length / block;
        if (blocks < 16)
        {
            throw new ArgumentException("Device is too small to probe.", nameof(device));
        }

        // Collisions between aliased blocks follow the birthday rule, so the number of points grows with sqrt(capacity).
        var count = probePoints ?? (int)Math.Clamp(Math.Sqrt(blocks) * 6, 1024, 16384);
        count = (int)Math.Min(count, blocks);
        var offsets = PickOffsets(blocks, count, seed).Select(b => b * block).ToArray();

        using var buffer = new AlignedBuffer(block, device.BufferAlignment);
        var failed = new List<(long Offset, long? FoundTagOffset)>();

        var writeWatch = Stopwatch.StartNew();
        for (var i = 0; i < offsets.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FillTagged(buffer.GetSpan(), offsets[i], seed);
            device.Write(offsets[i], buffer.GetSpan());
            if (i % 64 == 0)
            {
                progress?.Report(0.5 * i / offsets.Length);
                await Task.Yield();
            }
        }

        device.Flush();
        writeWatch.Stop();

        var readWatch = Stopwatch.StartNew();
        for (var i = 0; i < offsets.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var span = buffer.GetSpan();
            span.Clear();
            var read = device.Read(offsets[i], span);
            long? found = null;
            if (read < block || !IsTagged(span, offsets[i], seed, out found))
            {
                failed.Add((offsets[i], found));
            }

            if (i % 64 == 0)
            {
                progress?.Report(0.5 + 0.5 * i / offsets.Length);
                await Task.Yield();
            }
        }

        readWatch.Stop();
        progress?.Report(1);

        var megabytes = offsets.Length * (double)block / (1024 * 1024);
        return new CapacityProbeResult(
            failed.Count == 0,
            device.Length,
            failed.Count == 0 ? device.Length : EstimateRealSize(failed, block),
            failed.Count == 0 ? null : failed.Min(f => f.Offset),
            offsets.Length,
            failed.Count,
            megabytes / Math.Max(writeWatch.Elapsed.TotalSeconds, 0.001),
            megabytes / Math.Max(readWatch.Elapsed.TotalSeconds, 0.001));
    }

    /// <summary>Block 0, the last block and a pseudo-random spread over the rest; reproducible for a given seed.</summary>
    internal static IEnumerable<long> PickOffsets(long blocks, int count, int seed)
    {
        var picked = new SortedSet<long> { 0, blocks - 1 };
        var random = new Random(seed);
        while (picked.Count < count)
        {
            picked.Add(random.NextInt64(blocks));
        }

        return picked;
    }

    private static void FillTagged(Span<byte> block, long offset, int seed)
    {
        BinaryPrimitives.WriteInt32LittleEndian(block, Magic);
        BinaryPrimitives.WriteInt64LittleEndian(block[4..], offset);
        BinaryPrimitives.WriteInt32LittleEndian(block[12..], seed);

        // Content derived from the offset, so a block that is copied elsewhere cannot pass for another one.
        var state = (ulong)offset * 0x9E3779B97F4A7C15UL + (ulong)seed;
        for (var i = HeaderBytes; i + 8 <= block.Length; i += 8)
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            BinaryPrimitives.WriteUInt64LittleEndian(block[i..], state);
        }
    }

    private static bool IsTagged(ReadOnlySpan<byte> block, long expectedOffset, int seed, out long? foundOffset)
    {
        foundOffset = null;
        if (BinaryPrimitives.ReadInt32LittleEndian(block) == Magic && BinaryPrimitives.ReadInt32LittleEndian(block[12..]) == seed)
        {
            foundOffset = BinaryPrimitives.ReadInt64LittleEndian(block[4..]);
        }

        if (foundOffset != expectedOffset)
        {
            return false;
        }

        Span<byte> reference = stackalloc byte[block.Length <= 8192 ? block.Length : 0];
        if (reference.Length == 0)
        {
            return true;
        }

        FillTagged(reference, expectedOffset, seed);
        return block.SequenceEqual(reference);
    }

    /// <summary>
    /// A wrapped block carries the tag of a block that lives a multiple of the real size away, so the
    /// greatest common divisor of those distances is the real capacity (or a divisor of it).
    /// </summary>
    private static long? EstimateRealSize(List<(long Offset, long? FoundTagOffset)> failed, int block)
    {
        long gcd = 0;
        foreach (var (offset, found) in failed)
        {
            if (found is { } other && other != offset)
            {
                gcd = Gcd(gcd, Math.Abs(other - offset));
            }
        }

        return gcd >= 1024 * 1024 ? gcd / block * block : null;
    }

    private static long Gcd(long a, long b) => b == 0 ? a : Gcd(b, a % b);
}
