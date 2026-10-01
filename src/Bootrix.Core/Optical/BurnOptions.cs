// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;

namespace Bootrix.Core.Optical;

/// <summary>Values match IMAPI_BURN_VERIFICATION_LEVEL.</summary>
public enum BurnVerifyLevel
{
    None = 0,

    /// <summary>The drive checks the disc structure and a handful of sectors.</summary>
    Quick = 1,

    /// <summary>The drive reads the whole written session back and compares a checksum.</summary>
    Full = 2,
}

public sealed record BurnOptions
{
    /// <summary>Speed as multiple of 1x (16 for 16x); null lets the drive pick its fastest speed for the disc.</summary>
    public int? WriteSpeedFactor { get; init; }

    /// <summary>Close the disc so that no session can be added. Installation and boot media must be closed to work in every drive.</summary>
    public bool Finalize { get; init; } = true;

    public BurnVerifyLevel Verify { get; init; } = BurnVerifyLevel.Quick;

    /// <summary>Read the disc back through the operating system and compare it with the source by SHA-256, independent of the drive's own check.</summary>
    public bool ReadBackSha256 { get; init; }

    /// <summary>Buffer underrun protection; IMAPI allows switching it off for CD only, and there is rarely a reason to.</summary>
    public bool BufferUnderrunProtection { get; init; } = true;

    /// <summary>Only 2048 is accepted: IMAPI writes data discs in user-data sectors. Raw 2352-byte sectors would need the raw-CD interface, which Bootrix does not use.</summary>
    public int SectorSize { get; init; } = SectorMath.SectorSize;

    /// <summary>Volume label for discs built from a folder; images carry their own.</summary>
    public string? VolumeLabel { get; init; }

    /// <summary>Write into a rewritable disc that already holds data instead of asking for an erase first.</summary>
    public bool ForceOverwrite { get; init; }

    public bool EjectWhenDone { get; init; }

    public void Validate()
    {
        if (SectorSize != SectorMath.SectorSize)
        {
            throw new BootrixException(ErrorCode.InvalidSpec, $"sector size {SectorSize}")
            {
                Arguments = [$"SectorSize {SectorSize} is not supported; data discs are written with {SectorMath.SectorSize} bytes per sector"],
            };
        }

        if (WriteSpeedFactor is <= 0)
        {
            throw new BootrixException(ErrorCode.InvalidSpec, "write speed")
            {
                Arguments = ["WriteSpeedFactor must be greater than 0"],
            };
        }
    }
}

public enum EraseMode
{
    /// <summary>Clears only the table of contents; takes seconds and is enough to write the disc again.</summary>
    Quick,

    /// <summary>Overwrites the whole disc; takes as long as a full write, but also removes the old data.</summary>
    Full,
}
