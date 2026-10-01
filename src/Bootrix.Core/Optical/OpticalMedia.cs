// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Optical;

/// <summary>State bits of IMAPI_FORMAT2_DATA_MEDIA_STATE.</summary>
[Flags]
public enum OpticalMediaState
{
    Unknown = 0,
    OverwriteOnly = 0x1,
    Blank = 0x2,
    Appendable = 0x4,
    FinalSession = 0x8,
    Damaged = 0x400,
    EraseRequired = 0x800,
    NonEmptySession = 0x1000,
    WriteProtected = 0x2000,
    Finalized = 0x4000,
    UnsupportedMedia = 0x8000,

    /// <summary>IMAPI's UNSUPPORTED_MASK: every state it refuses to burn into without erasing or replacing the disc.</summary>
    UnsupportedMask = 0xFC00,
}

public enum OpticalMediaCondition
{
    NoMedia,

    /// <summary>A disc that cannot be written: ROM media, write protected, damaged or a type IMAPI does not know.</summary>
    NotWritable,

    Blank,

    /// <summary>Written but still open for another session.</summary>
    Appendable,

    /// <summary>Rewritable disc with content; it must be erased or overwritten.</summary>
    Rewritable,

    Closed,
}

/// <summary>One entry of the speed list a drive offers for the inserted disc.</summary>
public readonly record struct WriteSpeed(int SectorsPerSecond, bool PureCav)
{
    public int Factor(OpticalMediaFamily family) => SectorMath.SpeedFactor(family, SectorsPerSecond);
}

public sealed record OpticalMedia
{
    public static OpticalMedia None { get; } = new();

    public OpticalMediaType Type { get; init; }

    public OpticalMediaState State { get; init; }

    /// <summary>IMAPI's MediaHeuristicallyBlank; the state flag alone misses DVD+RW and other overwritable media.</summary>
    public bool HeuristicallyBlank { get; init; }

    public long FreeSectors { get; init; }

    public long TotalSectors { get; init; }

    public long NextWritableAddress { get; init; }

    public long StartOfPreviousSession { get; init; }

    public long LastWrittenOfPreviousSession { get; init; }

    public IReadOnlyList<WriteSpeed> WriteSpeeds { get; init; } = [];

    /// <summary>False when the recorder cannot write the inserted disc type.</summary>
    public bool IsSupported { get; init; }

    public bool IsPresent => Type != OpticalMediaType.Unknown || State != OpticalMediaState.Unknown;

    public OpticalMediaFamily Family => Type.Family();

    public bool IsRewritable => Type.IsRewritable();

    public bool IsBlank => State.HasFlag(OpticalMediaState.Blank) || HeuristicallyBlank;

    public long CapacityBytes => SectorMath.ToBytes(TotalSectors);

    public long FreeBytes => SectorMath.ToBytes(FreeSectors);

    /// <summary>Highest speed factor offered (for example 16 for 16x), or 0 when the drive lists none.</summary>
    public int MaxWriteSpeedFactor => WriteSpeeds.Count == 0 ? 0 : WriteSpeeds.Max(s => s.Factor(Family));

    /// <summary>True when a further session could be added; Bootrix does not append, it only reports it.</summary>
    public bool IsMultiSession => State.HasFlag(OpticalMediaState.Appendable) && State.HasFlag(OpticalMediaState.NonEmptySession);

    public OpticalMediaCondition Condition
    {
        get
        {
            if (!IsPresent)
            {
                return OpticalMediaCondition.NoMedia;
            }

            const OpticalMediaState Unwritable = OpticalMediaState.WriteProtected | OpticalMediaState.Damaged | OpticalMediaState.UnsupportedMedia;
            if (!IsSupported || !Type.IsWritable() || (State & Unwritable) != 0)
            {
                return OpticalMediaCondition.NotWritable;
            }

            if (IsBlank)
            {
                return OpticalMediaCondition.Blank;
            }

            if (IsRewritable)
            {
                return OpticalMediaCondition.Rewritable;
            }

            return State.HasFlag(OpticalMediaState.Appendable) ? OpticalMediaCondition.Appendable : OpticalMediaCondition.Closed;
        }
    }
}
