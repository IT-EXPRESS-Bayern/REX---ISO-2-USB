// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Bootrix.Core.Errors;

namespace Bootrix.Core.Images.Apple;

/// <summary>
/// The volume stored in a .sparseimage (UDSP): a 4096-byte header with a band table, followed by the bands
/// in the order they were first written. Bands that were never written read as zeros.
/// </summary>
public sealed class SparseImageStream : ReadOnlyVolumeStream
{
    private const int HeaderSize = 4096;
    private const int TableOffset = 64;
    private const int MaxBands = (HeaderSize - TableOffset) / 4;
    private const long MaxBandSectors = 1L << 21;
    private const long SectorSize = 512;

    private readonly RandomAccessSource _source;
    private readonly long _bandBytes;
    private readonly long[] _bandOffsets;

    private SparseImageStream(RandomAccessSource source, long length, long bandBytes, long[] bandOffsets)
        : base(length)
    {
        _source = source;
        _bandBytes = bandBytes;
        _bandOffsets = bandOffsets;
    }

    /// <summary>Size of one band in bytes.</summary>
    public long BandSize => _bandBytes;

    /// <summary>Number of bands that exist in the file; the rest of the volume reads as zeros.</summary>
    public int AllocatedBands => _bandOffsets.Count(offset => offset >= 0);

    public static SparseImageStream Open(string path) => Open(RandomAccessSource.OpenFile(path));

    public static SparseImageStream Open(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return Open(RandomAccessSource.FromStream(stream, leaveOpen));
    }

    protected override int ReadCore(long position, Span<byte> destination)
    {
        var band = (int)(position / _bandBytes);
        var offsetInBand = position % _bandBytes;
        var count = (int)Math.Min(destination.Length, _bandBytes - offsetInBand);
        destination = destination[..count];

        var fileOffset = _bandOffsets[band];
        if (fileOffset < 0)
        {
            destination.Clear();
        }
        else
        {
            _source.ReadExactlyAt(fileOffset + offsetInBand, destination);
        }

        return count;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !Disposed)
        {
            _source.Dispose();
        }

        base.Dispose(disposing);
    }

    private static SparseImageStream Open(RandomAccessSource source)
    {
        try
        {
            return Parse(source);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    private static SparseImageStream Parse(RandomAccessSource source)
    {
        ContainerSniffer.ThrowIfUnsupported(ContainerSniffer.Detect(source));

        Span<byte> header = stackalloc byte[HeaderSize];
        source.ReadPadded(0, header);
        if (!header.StartsWith("sprs"u8))
        {
            throw ImageErrors.Corrupt("sparse image header ('sprs') not found");
        }

        var sectorsPerBand = BinaryPrimitives.ReadUInt32BigEndian(header[8..]);
        var totalSectors = BinaryPrimitives.ReadUInt32BigEndian(header[16..]);
        if (sectorsPerBand == 0 || sectorsPerBand > MaxBandSectors || totalSectors == 0)
        {
            throw ImageErrors.Corrupt("sparse image header has an invalid band size or length");
        }

        var bandBytes = sectorsPerBand * SectorSize;
        var length = totalSectors * SectorSize;
        var bandCount = (totalSectors + sectorsPerBand - 1) / sectorsPerBand;

        // Larger images grow the header; that layout is not documented, and guessing it would copy wrong data.
        if (bandCount > MaxBands)
        {
            throw ImageErrors.Unsupported($"sparse image with {bandCount} bands (more than {MaxBands})");
        }

        var offsets = new long[bandCount];
        Array.Fill(offsets, -1);
        for (var slot = 0; slot < MaxBands; slot++)
        {
            var logical = BinaryPrimitives.ReadUInt32BigEndian(header[(TableOffset + (slot * 4))..]);
            if (logical == 0)
            {
                continue;
            }

            // Table entry n holds the 1-based number of the band stored at file position 4096 + n * band size.
            if (logical > bandCount || offsets[logical - 1] >= 0)
            {
                throw ImageErrors.Corrupt("sparse image band table is inconsistent");
            }

            var fileOffset = HeaderSize + (slot * bandBytes);
            var needed = Math.Min(bandBytes, length - ((logical - 1) * bandBytes));
            if (fileOffset + needed > source.Length)
            {
                throw ImageErrors.Truncated(fileOffset + needed, source.Length);
            }

            offsets[logical - 1] = fileOffset;
        }

        return new SparseImageStream(source, length, bandBytes, offsets);
    }
}
