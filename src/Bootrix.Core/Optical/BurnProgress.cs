// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Optical;

/// <summary>What the recorder is doing. Values 0 to 7 are IMAPI_FORMAT2_DATA_WRITE_ACTION.</summary>
public enum BurnPhase
{
    ValidatingMedia = 0,
    FormattingMedia = 1,
    InitializingHardware = 2,
    CalibratingPower = 3,
    Writing = 4,
    Finalizing = 5,
    Completed = 6,
    Verifying = 7,

    /// <summary>Reading the burned disc back through the operating system (not an IMAPI action).</summary>
    ReadBack = 100,
}

/// <param name="Phase">Current action of the recorder.</param>
/// <param name="StartSector">First sector of the write; the sector counters below are absolute.</param>
/// <param name="SectorCount">Sectors this write will cover.</param>
/// <param name="LastWrittenSector">Last sector the drive has accepted so far.</param>
/// <param name="Elapsed">Time since the burn started.</param>
/// <param name="Remaining">Estimate from the drive; null while it has none.</param>
/// <param name="BufferFillPercent">Fill level of IMAPI's system buffer; a value that stays low means the source cannot keep up with the drive.</param>
public readonly record struct BurnProgress(
    BurnPhase Phase,
    long StartSector,
    long SectorCount,
    long LastWrittenSector,
    TimeSpan Elapsed,
    TimeSpan? Remaining,
    int BufferFillPercent)
{
    /// <summary>Builds the progress from the raw values of an IDiscFormat2DataEventArgs notification.</summary>
    public static BurnProgress FromWriteEvent(
        int action,
        int startLba,
        int sectorCount,
        int lastWrittenLba,
        int elapsedSeconds,
        int remainingSeconds,
        int totalSystemBuffer,
        int usedSystemBuffer)
    {
        var phase = Enum.IsDefined((BurnPhase)action) ? (BurnPhase)action : BurnPhase.ValidatingMedia;
        var fill = totalSystemBuffer > 0 ? (int)Math.Clamp(usedSystemBuffer * 100L / totalSystemBuffer, 0, 100) : 0;
        return new BurnProgress(
            phase,
            startLba,
            sectorCount,
            lastWrittenLba,
            TimeSpan.FromSeconds(Math.Max(0, elapsedSeconds)),
            remainingSeconds > 0 ? TimeSpan.FromSeconds(remainingSeconds) : null,
            fill);
    }

    public long SectorsWritten => SectorCount <= 0 ? 0 : Math.Clamp(LastWrittenSector - StartSector, 0, SectorCount);

    /// <summary>
    /// Share of the data already written. Everything before the write (calibration, formatting) counts as 0 and
    /// everything after it (finalizing, verification) as 1, so a bar driven by this value only moves while data goes out.
    /// </summary>
    public double Fraction => Phase switch
    {
        BurnPhase.Writing => SectorCount <= 0 ? 0 : (double)SectorsWritten / SectorCount,
        BurnPhase.Finalizing or BurnPhase.Completed or BurnPhase.Verifying => 1,
        _ => 0,
    };
}

public readonly record struct EraseProgress(TimeSpan Elapsed, TimeSpan EstimatedTotal)
{
    public double Fraction => EstimatedTotal <= TimeSpan.Zero ? 0 : Math.Clamp(Elapsed / EstimatedTotal, 0, 1);

    public TimeSpan? Remaining => EstimatedTotal <= TimeSpan.Zero ? null : TimeSpan.FromTicks(Math.Max(0, (EstimatedTotal - Elapsed).Ticks));
}
