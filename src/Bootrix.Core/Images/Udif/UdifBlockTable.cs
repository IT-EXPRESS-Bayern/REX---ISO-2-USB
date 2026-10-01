// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Bootrix.Core.Images.Apple;

namespace Bootrix.Core.Images.Udif;

/// <summary>One 40-byte entry of a block table, still in the raw on-disk units.</summary>
internal readonly record struct UdifChunkEntry(
    uint Type,
    ulong SectorNumber,
    ulong SectorCount,
    ulong CompressedOffset,
    ulong CompressedLength);

/// <summary>A "mish" block table: the chunk list of one partition of the image.</summary>
internal sealed class UdifBlockTable
{
    private const uint Signature = 0x6D697368;
    private const int HeaderSize = 0xCC;
    private const int EntrySize = 40;

    public ulong StartSector { get; private init; }

    public ulong SectorCount { get; private init; }

    public ulong DataOffset { get; private init; }

    public uint BuffersNeeded { get; private init; }

    public UdifChecksum Checksum { get; private init; }

    public UdifChunkEntry[] Entries { get; private init; } = [];

    public static UdifBlockTable Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize || BinaryPrimitives.ReadUInt32BigEndian(data) != Signature)
        {
            throw ImageErrors.Corrupt("block table has no 'mish' signature");
        }

        var version = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        if (version != 1)
        {
            throw ImageErrors.Unsupported($"block table version {version}");
        }

        // The entry count sits behind the 136-byte checksum; 7-Zip checks it against the blob size.
        var count = BinaryPrimitives.ReadUInt32BigEndian(data[0xC8..]);
        if ((long)count * EntrySize + HeaderSize > data.Length)
        {
            throw ImageErrors.Corrupt("block table claims more entries than it contains");
        }

        var entries = new UdifChunkEntry[count];
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = data.Slice(HeaderSize + (i * EntrySize), EntrySize);
            entries[i] = new UdifChunkEntry(
                BinaryPrimitives.ReadUInt32BigEndian(entry),
                BinaryPrimitives.ReadUInt64BigEndian(entry[8..]),
                BinaryPrimitives.ReadUInt64BigEndian(entry[16..]),
                BinaryPrimitives.ReadUInt64BigEndian(entry[24..]),
                BinaryPrimitives.ReadUInt64BigEndian(entry[32..]));
        }

        return new UdifBlockTable
        {
            StartSector = BinaryPrimitives.ReadUInt64BigEndian(data[8..]),
            SectorCount = BinaryPrimitives.ReadUInt64BigEndian(data[0x10..]),
            DataOffset = BinaryPrimitives.ReadUInt64BigEndian(data[0x18..]),
            BuffersNeeded = BinaryPrimitives.ReadUInt32BigEndian(data[0x20..]),
            Checksum = UdifChecksum.Parse(data[0x40..]),
            Entries = entries,
        };
    }
}
