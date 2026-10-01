// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace Bootrix.Core.Images.Udif;

/// <summary>
/// The 136-byte checksum record of UDIF: type, size in bits and 128 bytes of data.
/// Only CRC-32 (type 2) is interpreted; its value is the first data word, big endian.
/// </summary>
public readonly record struct UdifChecksum(uint Type, uint Bits, uint Crc32)
{
    public const int Size = 136;
    public const uint Crc32Type = 2;

    public bool IsPresent => Type != 0;

    public bool IsCrc32 => Type == Crc32Type && Bits == 32;

    internal static UdifChecksum Parse(ReadOnlySpan<byte> data) => new(
        BinaryPrimitives.ReadUInt32BigEndian(data),
        BinaryPrimitives.ReadUInt32BigEndian(data[4..]),
        BinaryPrimitives.ReadUInt32BigEndian(data[8..]));
}
