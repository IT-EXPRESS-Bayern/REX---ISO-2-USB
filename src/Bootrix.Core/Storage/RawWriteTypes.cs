// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Storage;

public enum RawWritePhase
{
    Writing,
    Verifying,
}

public readonly record struct RawWriteProgress(RawWritePhase Phase, long BytesDone, long BytesTotal);

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
}

public sealed record RawTargetResult(IBlockDevice Device, bool Succeeded, Exception? Error, long BytesWritten, bool Verified);

public sealed record RawWriteReport(long ImageBytes, string Sha256, IReadOnlyList<RawTargetResult> Targets)
{
    public bool AllSucceeded => Targets.All(t => t.Succeeded);
}
