// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers;
using Bootrix.Core.Images.Apple;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Core.Images.Udif;

/// <summary>
/// The decoded volume of an Apple UDIF disk image (.dmg) as a read-only, seekable stream.
/// Chunks are decompressed on demand and kept in a small cache; sequential reads decode the following
/// chunks on other threads. Corrupt or hostile images fail with a <see cref="Errors.BootrixException"/>.
/// </summary>
public sealed class DmgReader : ReadOnlyVolumeStream
{
    public const int SectorSize = 512;

    private readonly RandomAccessSource _source;
    private readonly DmgLayout _layout;
    private readonly ChunkPipeline _pipeline;
    private int _lookupHint;

    private DmgReader(RandomAccessSource source, DmgLayout layout, DmgReaderOptions options)
        : base(layout.VolumeSectors * SectorSize)
    {
        _source = source;
        _layout = layout;
        _pipeline = new ChunkPipeline(layout.Chunks, options, Decode);
        Info = BuildInfo(layout);
    }

    public DmgInfo Info { get; }

    public static DmgReader Open(string path, DmgReaderOptions? options = null, ILogger? logger = null) =>
        Open(RandomAccessSource.OpenFile(path), options, logger);

    /// <summary>Opens an image from a seekable stream; the reader takes ownership unless <paramref name="leaveOpen"/> is set.</summary>
    public static DmgReader Open(Stream stream, bool leaveOpen = false, DmgReaderOptions? options = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek || !stream.CanRead)
        {
            throw new ArgumentException("The stream must be readable and seekable.", nameof(stream));
        }

        return Open(RandomAccessSource.FromStream(stream, leaveOpen), options, logger);
    }

    /// <summary>
    /// Recomputes the data fork, partition and master checksums. This decodes the whole image,
    /// so it is a separate step rather than part of every read.
    /// </summary>
    public DmgChecksumReport VerifyChecksums(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        return DmgVerifier.Verify(_layout, _source, _pipeline.Get, cancellationToken);
    }

    protected override int ReadCore(long position, Span<byte> destination)
    {
        var sector = position / SectorSize;
        var index = FindChunk(sector);
        if (index < 0)
        {
            // Not covered by any chunk: reads as zeros up to the next chunk.
            var next = ~index;
            var gapEnd = next < _layout.Chunks.Length ? _layout.Chunks[next].Sector * SectorSize : Length;
            var gap = (int)Math.Min(destination.Length, gapEnd - position);
            destination[..gap].Clear();
            return gap;
        }

        _lookupHint = index;
        var chunk = _layout.Chunks[index];
        var offset = position - (chunk.Sector * SectorSize);
        var count = (int)Math.Min(destination.Length, (chunk.SectorCount * SectorSize) - offset);
        destination = destination[..count];

        switch (chunk.Type)
        {
            case UdifChunkType.ZeroFill:
            case UdifChunkType.Ignore:
                destination.Clear();
                break;
            case UdifChunkType.Raw:
                _source.ReadExactlyAt(chunk.FileOffset + offset, destination);
                break;
            default:
                _pipeline.Get(index).AsSpan((int)offset, count).CopyTo(destination);
                break;
        }

        return count;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !Disposed)
        {
            _pipeline.Dispose();
            _source.Dispose();
        }

        base.Dispose(disposing);
    }

    private static DmgReader Open(RandomAccessSource source, DmgReaderOptions? options, ILogger? logger)
    {
        options ??= new DmgReaderOptions();
        try
        {
            var layout = DmgLayout.Read(source, options, logger ?? NullLogger.Instance);
            return new DmgReader(source, layout, options);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    private static DmgInfo BuildInfo(DmgLayout layout) => new()
    {
        VolumeSize = layout.VolumeSectors * SectorSize,
        SectorCount = layout.VolumeSectors,
        ImageVariant = layout.Trailer.ImageVariant,
        Flags = layout.Trailer.Flags,
        TrailerAtFront = layout.Trailer.AtFront,
        UsesResourceFork = layout.UsesResourceFork,
        Partitions = layout.Partitions,
        ChunkTypes = [.. layout.Chunks.Select(c => c.Type).Distinct().Order()],
        ChunkCount = layout.Chunks.Length,
        DataForkLength = (long)Math.Min(layout.Trailer.DataForkLength, long.MaxValue),
    };

    // Returns the index of the chunk containing the sector, or the bitwise complement of the next chunk's index.
    private int FindChunk(long sector)
    {
        var chunks = _layout.Chunks;
        var hint = _lookupHint;
        if ((uint)hint < (uint)chunks.Length)
        {
            if (Contains(chunks[hint], sector))
            {
                return hint;
            }

            if (hint + 1 < chunks.Length && Contains(chunks[hint + 1], sector))
            {
                return hint + 1;
            }
        }

        var low = 0;
        var high = chunks.Length - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (sector < chunks[middle].Sector)
            {
                high = middle - 1;
            }
            else if (sector >= chunks[middle].EndSector)
            {
                low = middle + 1;
            }
            else
            {
                return middle;
            }
        }

        return ~low;
    }

    private static bool Contains(in UdifChunk chunk, long sector) => sector >= chunk.Sector && sector < chunk.EndSector;

    // Reads and decompresses one chunk; safe to run on several threads at once.
    private byte[] Decode(int index, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var chunk = _layout.Chunks[index];
        var output = GC.AllocateUninitializedArray<byte>(checked((int)(chunk.SectorCount * SectorSize)));
        var input = ArrayPool<byte>.Shared.Rent((int)chunk.CompressedLength);
        try
        {
            _source.ReadExactlyAt(chunk.FileOffset, input.AsSpan(0, (int)chunk.CompressedLength));
            cancellationToken.ThrowIfCancellationRequested();
            ChunkDecoder.Decode(chunk.Type, input, (int)chunk.CompressedLength, output);
            return output;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(input);
        }
    }
}
