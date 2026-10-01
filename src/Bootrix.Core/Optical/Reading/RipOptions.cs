// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Optical.Reading;

public sealed record RipOptions
{
    /// <summary>Sectors per read request in the normal fast mode; 64 sectors are 128 KiB.</summary>
    public int ChunkSectors { get; init; } = 64;

    /// <summary>How often a failed chunk is read again as a whole before the ripper falls back to single sectors.</summary>
    public int ChunkRetries { get; init; } = 3;

    /// <summary>Attempts per sector in single-sector mode before it is given up and zero-filled.</summary>
    public int SectorRetries { get; init; } = 3;

    /// <summary>Pause before every retry; a drive that just failed often needs a moment before it can be spun up again.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Read speed requested from the drive after the first error (KB/s); null keeps the speed. Slower reads rescue many marginal sectors.</summary>
    public int? ErrorReadSpeedKilobytesPerSecond { get; init; }

    /// <summary>The rip stops with an error once this many sectors are unreadable; a disc that bad is not worth hours of retries.</summary>
    public long MaxBadSectors { get; init; } = 65_536;

    /// <summary>Stop at the end of the ISO 9660 volume instead of the capacity the drive reports. For rewritable media, where the formatted size is much larger than the data.</summary>
    public bool TrimToFileSystem { get; init; }

    /// <summary>Continue a partial image if a checkpoint matches the disc in the drive. Checkpoints are written either way.</summary>
    public bool Resume { get; init; } = true;

    /// <summary>Minimum time between checkpoint writes.</summary>
    public TimeSpan CheckpointInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Checkpoint file; defaults to the image path with ".btxrip" appended.</summary>
    public string? CheckpointPath { get; init; }
}

public enum RipPhase
{
    Analyzing,

    /// <summary>Hashing what an earlier run already wrote, after a resume.</summary>
    Resuming,

    Reading,
    Finishing,
}

public readonly record struct RipProgress(
    RipPhase Phase,
    long SectorsDone,
    long SectorsTotal,
    long BadSectors,
    double BytesPerSecond,
    TimeSpan? Remaining)
{
    public double Fraction => SectorsTotal <= 0 ? 0 : Math.Clamp((double)SectorsDone / SectorsTotal, 0, 1);
}
