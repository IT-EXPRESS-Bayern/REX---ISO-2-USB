// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Bootrix.Core.Images.Apple;

namespace Bootrix.Core.Images.Udif;

/// <summary>The 512-byte "koly" block of a UDIF image (all fields big endian).</summary>
internal sealed record UdifTrailer
{
    public const int Size = 512;
    public const uint Signature = 0x6B6F6C79;

    // MacBinary wrappers put a 128-byte header in front of the data fork.
    private const int MacBinaryHeaderSize = 128;

    public uint Version { get; init; }

    public uint Flags { get; init; }

    public ulong DataForkOffset { get; init; }

    public ulong DataForkLength { get; init; }

    public ulong ResourceForkOffset { get; init; }

    public ulong ResourceForkLength { get; init; }

    public uint SegmentNumber { get; init; }

    public uint SegmentCount { get; init; }

    public UdifChecksum DataChecksum { get; init; }

    public ulong XmlOffset { get; init; }

    public ulong XmlLength { get; init; }

    public UdifChecksum MasterChecksum { get; init; }

    public uint ImageVariant { get; init; }

    public ulong SectorCount { get; init; }

    /// <summary>Added to every offset stored in the block; non-zero when the block sits at the front.</summary>
    public long BaseOffset { get; init; }

    public bool AtFront { get; init; }

    public static bool HasSignature(ReadOnlySpan<byte> data) =>
        data.Length >= 4 && BinaryPrimitives.ReadUInt32BigEndian(data) == Signature;

    public static UdifTrailer Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < Size || !HasSignature(data))
        {
            throw ImageErrors.Corrupt("UDIF trailer is missing its signature");
        }

        var version = BinaryPrimitives.ReadUInt32BigEndian(data[4..]);
        if (version != 4)
        {
            throw ImageErrors.Unsupported($"UDIF version {version}");
        }

        if (BinaryPrimitives.ReadUInt32BigEndian(data[8..]) != Size)
        {
            throw ImageErrors.Corrupt("UDIF trailer has an unexpected header size");
        }

        return new UdifTrailer
        {
            Version = version,
            Flags = BinaryPrimitives.ReadUInt32BigEndian(data[0x0C..]),
            DataForkOffset = BinaryPrimitives.ReadUInt64BigEndian(data[0x18..]),
            DataForkLength = BinaryPrimitives.ReadUInt64BigEndian(data[0x20..]),
            ResourceForkOffset = BinaryPrimitives.ReadUInt64BigEndian(data[0x28..]),
            ResourceForkLength = BinaryPrimitives.ReadUInt64BigEndian(data[0x30..]),
            SegmentNumber = BinaryPrimitives.ReadUInt32BigEndian(data[0x38..]),
            SegmentCount = BinaryPrimitives.ReadUInt32BigEndian(data[0x3C..]),
            DataChecksum = UdifChecksum.Parse(data[0x50..]),
            XmlOffset = BinaryPrimitives.ReadUInt64BigEndian(data[0xD8..]),
            XmlLength = BinaryPrimitives.ReadUInt64BigEndian(data[0xE0..]),
            MasterChecksum = UdifChecksum.Parse(data[0x160..]),
            ImageVariant = BinaryPrimitives.ReadUInt32BigEndian(data[0x1E8..]),
            SectorCount = BinaryPrimitives.ReadUInt64BigEndian(data[0x1EC..]),
        };
    }

    /// <summary>Looks for the block at the end of the file first, then at the front (also behind a MacBinary header).</summary>
    public static UdifTrailer? Find(RandomAccessSource source)
    {
        Span<byte> block = stackalloc byte[Size];

        if (source.Length >= Size)
        {
            source.ReadExactlyAt(source.Length - Size, block);
            if (HasSignature(block))
            {
                return Parse(block);
            }
        }

        foreach (var position in new[] { 0, MacBinaryHeaderSize })
        {
            if (source.Length < position + Size)
            {
                continue;
            }

            source.ReadExactlyAt(position, block);
            if (HasSignature(block))
            {
                return Parse(block) with { BaseOffset = position, AtFront = true };
            }
        }

        return null;
    }
}
