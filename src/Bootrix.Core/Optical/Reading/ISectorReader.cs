// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Optical.Reading;

public enum SectorReadStatus
{
    /// <summary>No error. Fewer sectors than requested may still come back (a short read); ask for the rest.</summary>
    Ok,

    /// <summary>The sector right after the ones delivered cannot be read.</summary>
    Unreadable,

    /// <summary>The drive refuses the sector because it is scrambled copy-protected content.</summary>
    ProtectedContent,
}

/// <param name="SectorsRead">Sectors at the start of the request that are valid in the buffer.</param>
/// <param name="Status">Why the read stopped; <see cref="SectorReadStatus.Ok"/> means nothing went wrong.</param>
public readonly record struct SectorReadResult(int SectorsRead, SectorReadStatus Status)
{
    public static SectorReadResult Success(int sectors) => new(sectors, SectorReadStatus.Ok);

    public static SectorReadResult Failure(int sectorsBefore, SectorReadStatus status = SectorReadStatus.Unreadable) => new(sectorsBefore, status);
}

/// <summary>
/// Reads 2048-byte sectors from a disc. A defect is not an exception but a result, because a
/// scratched disc produces thousands of them; losing the disc or the drive is an exception
/// (<see cref="Errors.BootrixException"/>) since nothing can be done about it.
/// </summary>
public interface ISectorReader : IDisposable
{
    string Name { get; }

    /// <summary>Capacity in sectors as reported by the drive; for rewritable media this is the formatted size, not that of the file system.</summary>
    long SectorCount { get; }

    /// <summary>Reads up to <paramref name="count"/> sectors into <paramref name="buffer"/>, which must hold at least that many; any alignment the device needs is the reader's business.</summary>
    SectorReadResult Read(long lba, int count, Span<byte> buffer);

    /// <summary>Asks the drive to read slower, which often rescues marginal sectors. Returns false when the drive or the reader cannot.</summary>
    bool TrySetReadSpeed(int kilobytesPerSecond) => false;
}

/// <summary>Disc structure queries that only some readers can answer; a reader that has none of them simply does not implement this.</summary>
public interface IDiscInspector
{
    /// <summary>Table of contents of a CD, or null for DVD/BD and when the drive does not answer.</summary>
    DiscToc? ReadToc();

    /// <summary>Copyright information of a DVD, or null when it cannot be read.</summary>
    DiscCopyrightInfo? ReadCopyrightInfo();
}
