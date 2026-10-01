// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Images.Udif;

public sealed record DmgReaderOptions
{
    /// <summary>Memory for decoded chunks that stay available for repeated or backwards reads.</summary>
    public long CacheBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>
    /// Chunks decoded ahead of the read position on other threads during sequential reads.
    /// 0 disables read-ahead.
    /// </summary>
    public int ReadAhead { get; init; } = Math.Clamp(Environment.ProcessorCount - 1, 0, 4);

    /// <summary>Largest decoded volume accepted; larger sector counts are treated as damage.</summary>
    public long MaxVolumeBytes { get; init; } = 16L * 1024 * 1024 * 1024 * 1024;

    /// <summary>
    /// Largest decoded size of a single compressed chunk. Real images use 1 MiB; the limit keeps a
    /// manipulated sector count from allocating huge buffers.
    /// </summary>
    public int MaxChunkBytes { get; init; } = 64 * 1024 * 1024;
}
