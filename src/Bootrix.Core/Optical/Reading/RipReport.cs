// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Optical.Reading;

/// <summary>Things worth telling the user that are not errors.</summary>
public enum RipNote
{
    /// <summary>The disc has several sessions; the image holds all of them, including the unreadable gaps between them.</summary>
    MultiSession,

    /// <summary>The image ends at the end of the file system, not at the capacity the drive reported.</summary>
    TrimmedToFileSystem,

    /// <summary>The drive reported no usable capacity; the size comes from the file system.</summary>
    CapacityFromFileSystem,

    /// <summary>Read speed was lowered after the first error.</summary>
    SpeedReduced,

    /// <summary>A checkpoint existed but did not match the disc; the rip started again from the beginning.</summary>
    CheckpointDiscarded,
}

/// <param name="SectorCount">Sectors in the image, including zero-filled ones.</param>
/// <param name="Sha256">Hash of the whole image as written (unreadable sectors count as zeros), lower-case hex.</param>
/// <param name="BadSectors">Sectors that could not be read after all retries.</param>
/// <param name="ResumedFromSector">Where a continued rip picked up; 0 for a fresh one.</param>
public sealed record RipReport(
    long SectorCount,
    string Sha256,
    BadSectorMap BadSectors,
    long ResumedFromSector,
    TimeSpan Duration,
    DiscToc? Toc,
    IReadOnlyList<RipNote> Notes)
{
    public long Bytes => SectorMath.ToBytes(SectorCount);

    /// <summary>True when every sector was read, so the image is an exact copy of the disc.</summary>
    public bool IsComplete => BadSectors.IsEmpty;
}
