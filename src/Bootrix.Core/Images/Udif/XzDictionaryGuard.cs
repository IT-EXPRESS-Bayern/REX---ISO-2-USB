// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Bootrix.Core.Images.Apple;

namespace Bootrix.Core.Images.Udif;

/// <summary>
/// Reads just enough of the XZ container to find the LZMA2 dictionary size of every block.
/// The decoder allocates the whole dictionary before it reads any data, so a hostile header could
/// otherwise demand gigabytes for a chunk of one megabyte.
/// </summary>
internal static class XzDictionaryGuard
{
    private const int StreamHeaderSize = 12;
    private const int StreamFooterSize = 12;
    private const byte Lzma2FilterId = 0x21;

    private static ReadOnlySpan<byte> Magic => [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00];

    public static void EnsureDictionaryWithin(ReadOnlySpan<byte> xz, long maxDictionary)
    {
        if (xz.Length < StreamHeaderSize + StreamFooterSize || !xz.StartsWith(Magic))
        {
            throw ImageErrors.Corrupt("chunk is not an XZ stream");
        }

        var footer = xz[^StreamFooterSize..];
        if (!footer[10..].SequenceEqual("YZ"u8))
        {
            throw ImageErrors.Corrupt("XZ stream footer is damaged");
        }

        var indexSize = (BinaryPrimitives.ReadUInt32LittleEndian(footer[4..]) + 1L) * 4;
        var indexStart = xz.Length - StreamFooterSize - indexSize;
        if (indexStart < StreamHeaderSize || xz[(int)indexStart] != 0)
        {
            throw ImageErrors.Corrupt("XZ index is damaged");
        }

        var index = xz.Slice((int)indexStart, (int)indexSize);
        var position = 1;
        var records = ReadVli(index, ref position);
        if (records > (ulong)index.Length)
        {
            throw ImageErrors.Corrupt("XZ index lists too many blocks");
        }

        long blockStart = StreamHeaderSize;
        for (ulong i = 0; i < records; i++)
        {
            var unpaddedSize = ReadVli(index, ref position);
            _ = ReadVli(index, ref position);
            if (blockStart >= indexStart || unpaddedSize > (ulong)xz.Length)
            {
                throw ImageErrors.Corrupt("XZ index points outside the stream");
            }

            CheckBlockHeader(xz[(int)blockStart..], maxDictionary);
            blockStart += (long)((unpaddedSize + 3) & ~3UL);
        }
    }

    private static void CheckBlockHeader(ReadOnlySpan<byte> block, long maxDictionary)
    {
        var headerSize = (block[0] + 1) * 4;
        if (block[0] == 0 || headerSize > block.Length)
        {
            throw ImageErrors.Corrupt("XZ block header is damaged");
        }

        var header = block[..headerSize];
        var flags = header[1];
        var position = 2;
        if ((flags & 0x40) != 0)
        {
            _ = ReadVli(header, ref position);
        }

        if ((flags & 0x80) != 0)
        {
            _ = ReadVli(header, ref position);
        }

        var filters = (flags & 0x03) + 1;
        for (var i = 0; i < filters; i++)
        {
            var id = ReadVli(header, ref position);
            var propertySize = ReadVli(header, ref position);
            if (propertySize > (ulong)(header.Length - position))
            {
                throw ImageErrors.Corrupt("XZ filter properties are damaged");
            }

            if (id == Lzma2FilterId && propertySize == 1)
            {
                var dictionary = DictionarySize(header[position]);
                if (dictionary > maxDictionary)
                {
                    throw ImageErrors.Unsupported($"XZ dictionary of {ImageErrors.FormatSize(dictionary)}");
                }
            }

            position += (int)propertySize;
        }
    }

    // LZMA2 stores the dictionary as 2^(11 + b/2), plus half of that for odd b; 40 means 4 GiB - 1.
    private static long DictionarySize(byte property) => property switch
    {
        > 40 => throw ImageErrors.Corrupt("XZ dictionary size is invalid"),
        40 => uint.MaxValue,
        _ => (2L | (property & 1)) << ((property / 2) + 11),
    };

    private static ulong ReadVli(ReadOnlySpan<byte> data, ref int position)
    {
        ulong value = 0;
        for (var shift = 0; shift < 63; shift += 7)
        {
            if (position >= data.Length)
            {
                throw ImageErrors.Corrupt("XZ variable-length number is truncated");
            }

            var b = data[position++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return value;
            }
        }

        throw ImageErrors.Corrupt("XZ variable-length number is too long");
    }
}
