// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Partitioning;

/// <summary>A cylinder/head/sector address as packed into three bytes of an MBR partition entry.</summary>
public readonly record struct ChsAddress(int Cylinder, int Head, int Sector)
{
    public const int MaxCylinder = 1023;
    public const int MaxSector = 63;

    /// <summary>What an address beyond the 1023rd cylinder is written as; MS-DOS stops at head 254 because of a 256-head bug.</summary>
    public static ChsAddress Unrepresentable => new(MaxCylinder, 254, MaxSector);

    /// <summary>The end address of a protective MBR entry that does not fit (0xFFFFFF in the UEFI specification).</summary>
    public static ChsAddress ProtectiveEnd => new(MaxCylinder, 255, MaxSector);

    /// <summary>Where the protective entry starts: cylinder 0, head 0, sector 2 (LBA 1).</summary>
    public static ChsAddress ProtectiveStart => new(0, 0, 2);

    /// <summary>Byte 0 is the head, byte 1 holds the sector in bits 0-5 and cylinder bits 8-9 in bits 6-7, byte 2 the low cylinder byte.</summary>
    public void WriteTo(Span<byte> destination)
    {
        destination[0] = (byte)Head;
        destination[1] = (byte)((Sector & 0x3F) | ((Cylinder >> 8) & 0x03) << 6);
        destination[2] = (byte)Cylinder;
    }

    public static ChsAddress Read(ReadOnlySpan<byte> source) =>
        new(source[2] | (source[1] & 0xC0) << 2, source[0], source[1] & 0x3F);
}
