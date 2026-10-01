// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Storage;

public enum RawWritePhase
{
    Writing,
    Verifying,
}

public readonly record struct RawWriteProgress(RawWritePhase Phase, long BytesDone, long BytesTotal);

/// <summary>Sees every piece of the source in reading order, before it is written. Throwing aborts the write.</summary>
public delegate void SourceObserver(long offset, ReadOnlySpan<byte> data);

public sealed record RawWriteOptions
{
    /// <summary>Bytes per I/O request; rounded down to a multiple of every target's sector size.</summary>
    public int ChunkSize { get; init; } = 8 * 1024 * 1024;

    /// <summary>Number of chunks in flight. More buffers hide jitter of slow flash, fewer save memory.</summary>
    public int BufferCount { get; init; } = 4;

    /// <summary>
    /// The first bytes of the image are written last. A stick that is pulled or loses power half way
    /// then shows up as empty instead of as "bootable but broken".
    /// </summary>
    public int HoldBackBytes { get; init; } = 1024 * 1024;

    public bool Verify { get; init; } = true;

    /// <summary>Write only what a block map lists. The source is still read completely, because a compressed stream cannot skip.</summary>
    public SparseWriteMap? Sparse { get; init; }

    /// <summary>Checks the source while it is read, for instance against the checksums of a block map.</summary>
    public SourceObserver? SourceObserver { get; init; }
}

public sealed record RawTargetResult(IBlockDevice Device, bool Succeeded, Exception? Error, long BytesWritten, bool Verified);

public sealed record RawWriteReport(long ImageBytes, string Sha256, IReadOnlyList<RawTargetResult> Targets)
{
    /// <summary>Bytes of the image that were not written because a block map marks them as unused.</summary>
    public long SkippedBytes { get; init; }

    public bool AllSucceeded => Targets.All(t => t.Succeeded);
}
