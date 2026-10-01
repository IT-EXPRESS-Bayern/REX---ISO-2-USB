// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.IO.Compression;

namespace Bootrix.Core.Images.Compression;

/// <summary>
/// Cheap structural checks of compressed files: only headers and trailers are read, so the cost does
/// not depend on the size of the payload (zstd is the exception, it needs one small read per block).
/// </summary>
internal static class CompressedStructure
{
    private const int Bzip2TrailerBytes = 11;

    public static StructureReport Examine(Stream stream, CompressionFormat format, CancellationToken cancellationToken = default)
    {
        if (!stream.CanSeek)
        {
            return StructureReport.Unverifiable();
        }

        try
        {
            return format switch
            {
                CompressionFormat.Xz => XzStructure.Examine(stream),
                CompressionFormat.Zstd => ZstdStructure.Examine(stream, cancellationToken),
                CompressionFormat.BZip2 => ExamineBzip2(stream),
                CompressionFormat.GZip => ExamineGzip(stream),
                CompressionFormat.Lzma => ExamineLzma(stream),
                CompressionFormat.Zip => ExamineZip(stream),
                _ => StructureReport.Unverifiable(),
            };
        }
        catch (Exception ex) when (ex is EndOfStreamException or OverflowException or InvalidDataException)
        {
            return StructureReport.Broken($"{format}: {ex.Message}");
        }
    }

    /// <summary>
    /// The gzip trailer (CRC-32 and size modulo 2^32) cannot be validated without inflating the data;
    /// the size is therefore only a hint, and for several concatenated members it covers the last one.
    /// </summary>
    private static StructureReport ExamineGzip(Stream stream)
    {
        if (stream.Length < 18)
        {
            return StructureReport.Broken("gzip: shorter than header plus trailer");
        }

        Span<byte> trailer = stackalloc byte[4];
        stream.Position = stream.Length - 4;
        stream.ReadExactly(trailer);
        return StructureReport.Unverifiable(BinaryPrimitives.ReadUInt32LittleEndian(trailer));
    }

    /// <summary>
    /// The bzip2 stream ends with the 48-bit marker 0x177245385090 and a 32-bit CRC, followed by up to
    /// seven padding bits, so the marker can sit at any bit offset within the last 11 bytes.
    /// </summary>
    private static StructureReport ExamineBzip2(Stream stream)
    {
        if (stream.Length < 14)
        {
            return StructureReport.Broken("bzip2: shorter than header plus end marker");
        }

        Span<byte> tail = stackalloc byte[Bzip2TrailerBytes];
        stream.Position = stream.Length - Bzip2TrailerBytes;
        stream.ReadExactly(tail);

        UInt128 bits = 0;
        foreach (var b in tail)
        {
            bits = (bits << 8) | b;
        }

        for (var padding = 0; padding < 8; padding++)
        {
            if (((bits >> (32 + padding)) & 0xFFFFFFFFFFFFUL) == 0x177245385090UL)
            {
                return StructureReport.Unverifiable();
            }
        }

        return StructureReport.Broken("bzip2: end-of-stream marker not found");
    }

    private static StructureReport ExamineLzma(Stream stream)
    {
        Span<byte> header = stackalloc byte[13];
        stream.Position = 0;
        stream.ReadExactly(header);
        var size = BinaryPrimitives.ReadUInt64LittleEndian(header[5..]);
        return size == ulong.MaxValue
            ? StructureReport.Unverifiable()
            : new StructureReport(StructureVerdict.Unverifiable, UncompressedSize: (long)size);
    }

    private static StructureReport ExamineZip(Stream stream)
    {
        stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        return archive.Entries.Count == 0
            ? StructureReport.Broken("zip: no entries")
            : new StructureReport(StructureVerdict.Intact);
    }
}
