// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace Bootrix.Core.Partitioning;

/// <summary>One 16-byte slot of the MBR partition table.</summary>
public sealed record MbrEntry(
    byte Status,
    byte Type,
    uint StartLba,
    uint SectorCount,
    ChsAddress FirstChs,
    ChsAddress LastChs)
{
    public const int Size = 16;

    /// <summary>The only value old MBR code accepts as "boot from here".</summary>
    public const byte ActiveStatus = 0x80;

    public static MbrEntry Empty { get; } = new(0, MbrPartitionType.Empty, 0, 0, default, default);

    public bool IsEmpty => Type == MbrPartitionType.Empty;

    public bool IsActive => Status == ActiveStatus;

    /// <summary>First sector after the partition.</summary>
    public long EndLba => (long)StartLba + SectorCount;

    public static MbrEntry Read(ReadOnlySpan<byte> source) => new(
        source[0],
        source[4],
        BinaryPrimitives.ReadUInt32LittleEndian(source[8..]),
        BinaryPrimitives.ReadUInt32LittleEndian(source[12..]),
        ChsAddress.Read(source[1..]),
        ChsAddress.Read(source[5..]));

    public void WriteTo(Span<byte> destination)
    {
        destination[0] = Status;
        FirstChs.WriteTo(destination[1..]);
        destination[4] = Type;
        LastChs.WriteTo(destination[5..]);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], StartLba);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], SectorCount);
    }
}
