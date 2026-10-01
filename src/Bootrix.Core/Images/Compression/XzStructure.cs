// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.IO.Hashing;

namespace Bootrix.Core.Images.Compression;

/// <summary>
/// Reads the xz stream footers and block indexes from the end of a file, which gives the exact
/// uncompressed size and proves the file is not cut off. Concatenated streams (with stream padding)
/// are walked backwards until the start of the file is reached.
/// </summary>
internal static class XzStructure
{
    private const int HeaderSize = 12;
    private const int FooterSize = 12;
    private const int MaxIndexBytes = 64 * 1024 * 1024;

    public static StructureReport Examine(Stream stream)
    {
        var end = stream.Length;
        long total = 0;
        Span<byte> footer = stackalloc byte[FooterSize];
        Span<byte> header = stackalloc byte[HeaderSize];

        while (end > 0)
        {
            end = SkipStreamPadding(stream, end);
            if (end < HeaderSize + FooterSize)
            {
                return StructureReport.Broken("xz: file ends inside a stream or has no footer");
            }

            ReadAt(stream, end - FooterSize, footer);
            if (footer[10] != (byte)'Y' || footer[11] != (byte)'Z')
            {
                return StructureReport.Broken("xz: footer magic missing");
            }

            if (Crc32.HashToUInt32(footer[4..10]) != BinaryPrimitives.ReadUInt32LittleEndian(footer))
            {
                return StructureReport.Broken("xz: footer checksum mismatch");
            }

            var indexSize = (BinaryPrimitives.ReadUInt32LittleEndian(footer[4..]) + 1L) * 4;
            var indexStart = end - FooterSize - indexSize;
            if (indexSize > MaxIndexBytes || indexStart < HeaderSize)
            {
                return StructureReport.Broken("xz: index size out of range");
            }

            var index = new byte[indexSize];
            ReadAt(stream, indexStart, index);
            if (!TryParseIndex(index, out var blocksBytes, out var uncompressed))
            {
                return StructureReport.Broken("xz: index is corrupt");
            }

            var streamStart = indexStart - blocksBytes - HeaderSize;
            if (streamStart < 0)
            {
                return StructureReport.Broken("xz: block sizes exceed the file");
            }

            ReadAt(stream, streamStart, header);
            if (!header[..6].SequenceEqual<byte>([0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00])
                || !header[6..8].SequenceEqual(footer[8..10])
                || Crc32.HashToUInt32(header[6..8]) != BinaryPrimitives.ReadUInt32LittleEndian(header[8..]))
            {
                return StructureReport.Broken("xz: stream header does not match the footer");
            }

            total = checked(total + uncompressed);
            end = streamStart;
        }

        return new StructureReport(StructureVerdict.Intact, total);
    }

    /// <summary>Stream padding is a run of zero bytes whose length is a multiple of four.</summary>
    private static long SkipStreamPadding(Stream stream, long end)
    {
        var limit = end;
        Span<byte> chunk = stackalloc byte[256];
        while (end > 0)
        {
            var take = (int)Math.Min(chunk.Length, end);
            ReadAt(stream, end - take, chunk[..take]);
            var last = chunk[..take].LastIndexOfAnyExcept((byte)0);
            if (last >= 0)
            {
                end = end - take + last + 1;
                return (limit - end) % 4 == 0 ? end : 0;
            }

            end -= take;
        }

        return 0;
    }

    private static bool TryParseIndex(ReadOnlySpan<byte> index, out long blocksBytes, out long uncompressed)
    {
        blocksBytes = 0;
        uncompressed = 0;
        if (index.Length < 8 || index[0] != 0 ||
            Crc32.HashToUInt32(index[..^4]) != BinaryPrimitives.ReadUInt32LittleEndian(index[^4..]))
        {
            return false;
        }

        var position = 1;
        if (!TryReadVarInt(index, ref position, out var records))
        {
            return false;
        }

        for (ulong i = 0; i < records; i++)
        {
            if (!TryReadVarInt(index, ref position, out var unpadded)
                || !TryReadVarInt(index, ref position, out var size)
                || unpadded > long.MaxValue / 2 || size > long.MaxValue / 2)
            {
                return false;
            }

            blocksBytes = checked(blocksBytes + (((long)unpadded + 3) & ~3L));
            uncompressed = checked(uncompressed + (long)size);
        }

        // The index is zero-padded to a multiple of four before its CRC.
        for (; position < index.Length - 4; position++)
        {
            if (index[position] != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryReadVarInt(ReadOnlySpan<byte> data, ref int position, out ulong value)
    {
        value = 0;
        for (var shift = 0; shift < 63 && position < data.Length; shift += 7)
        {
            var b = data[position++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static void ReadAt(Stream stream, long offset, Span<byte> buffer)
    {
        stream.Position = offset;
        stream.ReadExactly(buffer);
    }
}
