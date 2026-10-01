// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace Bootrix.Core.Partitioning;

internal sealed record GptHeaderData(
    long MyLba,
    long AlternateLba,
    long FirstUsableLba,
    long LastUsableLba,
    Guid DiskGuid,
    long EntriesLba,
    uint EntryCount,
    uint EntrySize,
    uint EntriesCrc);

/// <summary>Reads and writes the 92-byte GPT header (UEFI 2.10, section 5.3.2).</summary>
internal static class GptHeaderCodec
{
    public const int HeaderSize = 92;
    public const uint Revision = 0x00010000;

    private const int CrcOffset = 16;
    private static ReadOnlySpan<byte> Signature => "EFI PART"u8;

    /// <summary>Fills a whole sector: the header, its CRC and zeros for the reserved remainder.</summary>
    public static void Write(Span<byte> sector, GptHeaderData header)
    {
        sector.Clear();
        Signature.CopyTo(sector);
        BinaryPrimitives.WriteUInt32LittleEndian(sector[8..], Revision);
        BinaryPrimitives.WriteUInt32LittleEndian(sector[12..], HeaderSize);
        BinaryPrimitives.WriteInt64LittleEndian(sector[24..], header.MyLba);
        BinaryPrimitives.WriteInt64LittleEndian(sector[32..], header.AlternateLba);
        BinaryPrimitives.WriteInt64LittleEndian(sector[40..], header.FirstUsableLba);
        BinaryPrimitives.WriteInt64LittleEndian(sector[48..], header.LastUsableLba);
        header.DiskGuid.TryWriteBytes(sector[56..72]);
        BinaryPrimitives.WriteInt64LittleEndian(sector[72..], header.EntriesLba);
        BinaryPrimitives.WriteUInt32LittleEndian(sector[80..], header.EntryCount);
        BinaryPrimitives.WriteUInt32LittleEndian(sector[84..], header.EntrySize);
        BinaryPrimitives.WriteUInt32LittleEndian(sector[88..], header.EntriesCrc);

        // The CRC covers the header with its own field zeroed.
        BinaryPrimitives.WriteUInt32LittleEndian(sector[CrcOffset..], Crc32.Compute(sector[..HeaderSize]));
    }

    /// <summary>Returns null unless signature, revision, size, CRC and self-reference all check out.</summary>
    public static GptHeaderData? TryRead(ReadOnlySpan<byte> sector, long expectedLba)
    {
        if (sector.Length < HeaderSize || !sector[..8].SequenceEqual(Signature))
        {
            return null;
        }

        var size = BinaryPrimitives.ReadUInt32LittleEndian(sector[12..]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(sector[8..]) != Revision || size < HeaderSize || size > sector.Length)
        {
            return null;
        }

        Span<byte> copy = stackalloc byte[(int)size];
        sector[..(int)size].CopyTo(copy);
        var stored = BinaryPrimitives.ReadUInt32LittleEndian(copy[CrcOffset..]);
        copy.Slice(CrcOffset, 4).Clear();
        if (Crc32.Compute(copy) != stored)
        {
            return null;
        }

        var header = new GptHeaderData(
            BinaryPrimitives.ReadInt64LittleEndian(sector[24..]),
            BinaryPrimitives.ReadInt64LittleEndian(sector[32..]),
            BinaryPrimitives.ReadInt64LittleEndian(sector[40..]),
            BinaryPrimitives.ReadInt64LittleEndian(sector[48..]),
            new Guid(sector[56..72]),
            BinaryPrimitives.ReadInt64LittleEndian(sector[72..]),
            BinaryPrimitives.ReadUInt32LittleEndian(sector[80..]),
            BinaryPrimitives.ReadUInt32LittleEndian(sector[84..]),
            BinaryPrimitives.ReadUInt32LittleEndian(sector[88..]));

        return header.MyLba == expectedLba ? header : null;
    }
}
