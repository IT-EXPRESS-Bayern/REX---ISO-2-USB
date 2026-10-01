// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace Bootrix.Core.Images.Compression;

/// <summary>
/// Walks the frame and block headers of a zstd file (RFC 8878) without decoding any block. The walk
/// has to end exactly at the end of the file; the sizes of all frames add up to the uncompressed size
/// when every frame declares its content size.
/// </summary>
internal static class ZstdStructure
{
    private const uint FrameMagic = 0xFD2FB528;
    private const uint SkippableMask = 0xFFFFFFF0;
    private const uint SkippableMagic = 0x184D2A50;

    public static StructureReport Examine(Stream stream, CancellationToken cancellationToken = default)
    {
        var length = stream.Length;
        long position = 0;
        long total = 0;
        var sizeKnown = true;
        Span<byte> header = stackalloc byte[18];

        while (position < length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (length - position < 6)
            {
                return StructureReport.Broken("zstd: trailing bytes shorter than a frame header");
            }

            stream.Position = position;
            stream.ReadExactly(header[..4]);
            var magic = BinaryPrimitives.ReadUInt32LittleEndian(header);

            if ((magic & SkippableMask) == SkippableMagic)
            {
                stream.ReadExactly(header[..4]);
                position += 8 + BinaryPrimitives.ReadUInt32LittleEndian(header);
                continue;
            }

            if (magic != FrameMagic)
            {
                return StructureReport.Broken("zstd: bad frame magic");
            }

            var frame = ReadFrameHeader(stream, header);
            if (frame is null)
            {
                return StructureReport.Broken("zstd: frame header is invalid");
            }

            var (headerLength, contentSize, hasChecksum) = frame.Value;
            if (contentSize is { } size)
            {
                total = checked(total + size);
            }
            else
            {
                sizeKnown = false;
            }

            position += 4 + headerLength;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (position + 3 > length)
                {
                    return StructureReport.Broken("zstd: file ends inside a frame");
                }

                stream.Position = position;
                stream.ReadExactly(header[..3]);
                var blockHeader = header[0] | (header[1] << 8) | (header[2] << 16);
                var last = (blockHeader & 1) != 0;
                var type = (blockHeader >> 1) & 3;
                var blockSize = blockHeader >> 3;
                if (type == 3)
                {
                    return StructureReport.Broken("zstd: reserved block type");
                }

                // RLE blocks store a single byte whatever their decoded size is.
                position += 3 + (type == 1 ? 1 : blockSize);
                if (last)
                {
                    break;
                }
            }

            if (hasChecksum)
            {
                position += 4;
            }

            if (position > length)
            {
                return StructureReport.Broken("zstd: file ends inside the last block");
            }
        }

        return new StructureReport(StructureVerdict.Intact, sizeKnown ? total : null);
    }

    /// <summary>Parses the frame header that follows the magic; <paramref name="scratch"/> must hold 14 bytes.</summary>
    private static (int HeaderLength, long? ContentSize, bool HasChecksum)? ReadFrameHeader(Stream stream, Span<byte> scratch)
    {
        stream.ReadExactly(scratch[..1]);
        var descriptor = scratch[0];
        var sizeFlag = descriptor >> 6;
        var singleSegment = (descriptor & 0x20) != 0;
        var hasChecksum = (descriptor & 0x04) != 0;
        var dictionaryBytes = (descriptor & 3) switch { 0 => 0, 1 => 1, 2 => 2, _ => 4 };
        var sizeBytes = sizeFlag switch { 0 => singleSegment ? 1 : 0, 1 => 2, 2 => 4, _ => 8 };

        // Reserved bit must be zero.
        if ((descriptor & 0x08) != 0)
        {
            return null;
        }

        var windowBytes = singleSegment ? 0 : 1;
        var rest = windowBytes + dictionaryBytes + sizeBytes;
        stream.ReadExactly(scratch[..rest]);

        long? contentSize = null;
        if (sizeBytes > 0)
        {
            var field = scratch.Slice(windowBytes + dictionaryBytes, sizeBytes);
            ulong value = 0;
            for (var i = sizeBytes - 1; i >= 0; i--)
            {
                value = (value << 8) | field[i];
            }

            // The two-byte form is stored with an offset of 256.
            if (sizeBytes == 2)
            {
                value += 256;
            }

            if (value > long.MaxValue)
            {
                return null;
            }

            contentSize = (long)value;
        }

        return (1 + rest, contentSize, hasChecksum);
    }
}
