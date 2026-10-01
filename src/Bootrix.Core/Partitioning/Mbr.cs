// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;

namespace Bootrix.Core.Partitioning;

/// <summary>The first sector of a disk: 440 bytes of boot code, a disk signature and four partition slots.</summary>
public sealed record Mbr
{
    public const int SectorSize = 512;
    public const int BootstrapLength = 440;
    public const int EntryCount = 4;

    private const int SignatureOffset = 440;
    private const int CopyProtectOffset = 444;
    private const int TableOffset = 446;
    private const int MarkerOffset = 510;

    public ReadOnlyMemory<byte> Bootstrap { get; init; } = new byte[BootstrapLength];

    /// <summary>
    /// Windows identifies a disk by this number, so two disks with the same value cannot be online
    /// together. A protective MBR leaves it zero.
    /// </summary>
    public uint DiskSignature { get; init; }

    /// <summary>Two bytes after the signature; 0x5A5A marks a copy-protected disk, everything else writes zero.</summary>
    public ushort Reserved { get; init; }

    /// <summary>Always <see cref="EntryCount"/> slots; unused ones are <see cref="MbrEntry.Empty"/>.</summary>
    public IReadOnlyList<MbrEntry> Entries { get; init; } = [MbrEntry.Empty, MbrEntry.Empty, MbrEntry.Empty, MbrEntry.Empty];

    /// <summary>True when the first slot carries the GPT protective type, i.e. the disk is meant to be read as GPT.</summary>
    public bool IsProtective => Entries[0].Type == MbrPartitionType.GptProtective;

    public static bool TryParse(ReadOnlySpan<byte> sector, [NotNullWhen(true)] out Mbr? mbr)
    {
        mbr = null;
        if (sector.Length < SectorSize || sector[MarkerOffset] != 0x55 || sector[MarkerOffset + 1] != 0xAA)
        {
            return false;
        }

        var entries = new MbrEntry[EntryCount];
        for (var i = 0; i < entries.Length; i++)
        {
            entries[i] = MbrEntry.Read(sector[(TableOffset + i * MbrEntry.Size)..]);
        }

        mbr = new Mbr
        {
            Bootstrap = sector[..BootstrapLength].ToArray(),
            DiskSignature = BinaryPrimitives.ReadUInt32LittleEndian(sector[SignatureOffset..]),
            Reserved = BinaryPrimitives.ReadUInt16LittleEndian(sector[CopyProtectOffset..]),
            Entries = entries,
        };
        return true;
    }

    public static Mbr Parse(ReadOnlySpan<byte> sector) =>
        TryParse(sector, out var mbr) ? mbr : throw new InvalidDataException("The sector has no 0x55AA boot signature.");

    public byte[] ToBytes()
    {
        var sector = new byte[SectorSize];
        WriteTo(sector);
        return sector;
    }

    public void WriteTo(Span<byte> sector)
    {
        if (sector.Length < SectorSize)
        {
            throw new ArgumentException("An MBR needs a 512-byte buffer.", nameof(sector));
        }

        if (Bootstrap.Length > BootstrapLength)
        {
            throw new InvalidOperationException($"Boot code of {Bootstrap.Length} bytes does not fit the {BootstrapLength}-byte area.");
        }

        sector[..SectorSize].Clear();
        Bootstrap.Span.CopyTo(sector);
        BinaryPrimitives.WriteUInt32LittleEndian(sector[SignatureOffset..], DiskSignature);
        BinaryPrimitives.WriteUInt16LittleEndian(sector[CopyProtectOffset..], Reserved);
        for (var i = 0; i < Entries.Count && i < EntryCount; i++)
        {
            Entries[i].WriteTo(sector[(TableOffset + i * MbrEntry.Size)..]);
        }

        sector[MarkerOffset] = 0x55;
        sector[MarkerOffset + 1] = 0xAA;
    }
}
