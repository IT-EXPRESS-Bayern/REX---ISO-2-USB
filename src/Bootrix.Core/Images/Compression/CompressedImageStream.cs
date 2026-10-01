// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.IO.Compression;
using SharpCompress.Common;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.LZMA;
using SharpCompress.Compressors.Xz;
using SharpCompress.Compressors.ZStandard;
using SharpCompressMode = SharpCompress.Compressors.CompressionMode;

namespace Bootrix.Core.Images.Compression;

/// <summary>
/// Forward-only stream over the decoded contents of a compressed image. The format comes from the file's
/// magic bytes, not from its name. Failures of the decoders surface as <see cref="Errors.BootrixException"/>
/// so a corrupt download can never end up as a silently short device image.
/// </summary>
public sealed class CompressedImageStream : Stream
{
    private readonly Stream _decoder;
    private readonly SourceStream? _input;
    private readonly ZipArchive? _archive;
    private readonly Stream? _zipSource;
    private readonly bool _leaveOpen;
    private readonly long? _compressedLength;
    private readonly long? _entryCompressedLength;
    private long _produced;

    private CompressedImageStream(
        Stream decoder,
        CompressionFormat format,
        StructureReport structure,
        SourceStream input,
        long? compressedLength)
    {
        _decoder = decoder;
        Format = format;
        _input = input;
        _compressedLength = compressedLength;
        UncompressedLength = structure.UncompressedSize;
        UncompressedLengthHint = structure.SizeHint;
        Structure = structure;
    }

    private CompressedImageStream(
        Stream decoder,
        ZipArchive archive,
        Stream source,
        bool leaveOpen,
        ArchiveEntryInfo entry,
        IReadOnlyList<ArchiveEntryInfo> entries)
    {
        _decoder = decoder;
        _archive = archive;
        _zipSource = source;
        _leaveOpen = leaveOpen;
        Format = CompressionFormat.Zip;
        EntryName = entry.Name;
        Entries = entries;
        UncompressedLength = entry.Length;
        _entryCompressedLength = entry.CompressedLength;
        _compressedLength = source.Length;
    }

    public CompressionFormat Format { get; }

    /// <summary>
    /// Exact size of the decoded data when the container records it (xz index, zstd frame headers, zip entry,
    /// LZMA header); null when it can only be found out by decoding everything.
    /// </summary>
    public long? UncompressedLength { get; }

    /// <summary>gzip only: the ISIZE field, which is the size modulo 2^32 of the last member.</summary>
    public long? UncompressedLengthHint { get; }

    /// <summary>What the header and trailer check found; only populated for seekable sources.</summary>
    internal StructureReport Structure { get; } = StructureReport.Unverifiable();

    /// <summary>Name of the zip entry being read.</summary>
    public string? EntryName { get; }

    /// <summary>All files of a zip archive that were considered; empty for the other formats.</summary>
    public IReadOnlyList<ArchiveEntryInfo> Entries { get; } = [];

    public long? CompressedLength => _compressedLength;

    public long BytesProduced => _produced;

    /// <summary>
    /// Compressed bytes consumed so far. For zip it is an estimate derived from the share of the entry
    /// that has been decoded, because the archive reader does its own seeking.
    /// </summary>
    public long CompressedBytesConsumed =>
        _input?.Position
        ?? (UncompressedLength is > 0 && _entryCompressedLength is { } packed
            ? (long)((double)_produced / UncompressedLength.Value * packed)
            : 0);

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException("The decoded length is available as UncompressedLength when known.");

    public override long Position
    {
        get => _produced;
        set => throw new NotSupportedException();
    }

    public static CompressedImageStream Open(string path, CompressedImageOptions? options = null)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        try
        {
            return Open(file, options);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    public static Task<CompressedImageStream> OpenAsync(string path, CompressedImageOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Open(path, options), cancellationToken);

    /// <summary>Opens <paramref name="source"/> from its current position; the stream takes ownership unless <paramref name="leaveOpen"/> is set.</summary>
    public static CompressedImageStream Open(Stream source, CompressedImageOptions? options = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= new CompressedImageOptions();

        var start = source.CanSeek ? source.Position : 0;
        var input = new SourceStream(source, leaveOpen);
        try
        {
            return Open(input, source, start, options, leaveOpen);
        }
        catch
        {
            // Disposing the wrapper closes the source unless the caller keeps ownership.
            input.Dispose();
            throw;
        }
    }

    private static CompressedImageStream Open(SourceStream input, Stream source, long start, CompressedImageOptions options, bool leaveOpen)
    {
        Span<byte> head = stackalloc byte[CompressionSniffer.HeaderLength];
        var headLength = input.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        input.Rewind(headLength);
        var format = CompressionSniffer.Detect(head[..headLength]);
        if (format == CompressionFormat.None)
        {
            throw ImageFailures.Unsupported("not a compressed image (no known magic bytes)");
        }

        if (format == CompressionFormat.Zip)
        {
            if (!source.CanSeek)
            {
                throw ImageFailures.Unsupported("zip archives must be read from a seekable source");
            }

            source.Position = start;
            return OpenZip(source, leaveOpen, options.EntryName);
        }

        var structure = StructureReport.Unverifiable();
        if (source.CanSeek)
        {
            structure = CompressedStructure.Examine(source, format);
            source.Position = start + input.Pulled;
            if (structure.Verdict == StructureVerdict.Broken && options.CheckStructure)
            {
                throw ImageFailures.Unreadable($"{format} file is incomplete or damaged: {structure.Problem}");
            }
        }

        var decoder = CreateDecoder(input, format, ref structure);
        return new CompressedImageStream(decoder, format, structure, input, source.CanSeek ? source.Length - start : null);
    }

    private static Stream CreateDecoder(SourceStream input, CompressionFormat format, ref StructureReport structure)
    {
        try
        {
            switch (format)
            {
                case CompressionFormat.GZip:
                    return new GzipMemberStream(input);
                case CompressionFormat.BZip2:
                    return BZip2Stream.Create(input, SharpCompressMode.Decompress, decompressConcatenated: true);
                case CompressionFormat.Xz:
                    return new XzConcatenatedStream(input);
                case CompressionFormat.Zstd:
                    return new DecompressionStream(input, bufferSize: 0, checkEndOfStream: true, leaveOpen: true);
                case CompressionFormat.Compress:
                    return new LzwStream(input, leaveOpen: true);
                case CompressionFormat.Lzma:
                    return OpenLzma(input, ref structure);
                default:
                    throw ImageFailures.Unsupported(format.ToString());
            }
        }
        catch (Exception ex) when (IsDecoderFailure(ex))
        {
            throw ImageFailures.Unreadable($"{format}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// LZMA-alone: 5 property bytes, an 8-byte size (all ones when unknown, in which case the stream is
    /// closed by an end marker) and then the range coder data.
    /// </summary>
    private static LzmaStream OpenLzma(SourceStream input, ref StructureReport structure)
    {
        Span<byte> header = stackalloc byte[13];
        input.ReadExactly(header);
        var size = BinaryPrimitives.ReadInt64LittleEndian(header[5..]);
        if (size >= 0)
        {
            structure = structure with { UncompressedSize = size };
        }

        return LzmaStream.Create(header[..5].ToArray(), input.Borrow(failAtEnd: true), inputSize: -1, outputSize: size, leaveOpen: true);
    }

    private static CompressedImageStream OpenZip(Stream source, bool leaveOpen, string? entryName)
    {
        if (!source.CanSeek)
        {
            throw ImageFailures.Unsupported("zip archives must be read from a seekable source");
        }

        ZipArchive? archive = null;
        try
        {
            archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
            var candidates = ZipEntrySelector.Candidates(archive);
            var chosen = ZipEntrySelector.Select(candidates, entryName);
            if (chosen.IsEncrypted)
            {
                throw ImageFailures.Encrypted($"zip entry '{chosen.FullName}' is encrypted");
            }

            var info = new ArchiveEntryInfo(chosen.FullName, chosen.Length, chosen.CompressedLength);
            var entries = candidates.Select(entry => new ArchiveEntryInfo(entry.FullName, entry.Length, entry.CompressedLength)).ToList();
            return new CompressedImageStream(chosen.Open(), archive, source, leaveOpen, info, entries);
        }
        catch (Exception ex) when (IsDecoderFailure(ex))
        {
            archive?.Dispose();
            throw ImageFailures.Unreadable($"zip: {ex.Message}", ex);
        }
        catch
        {
            archive?.Dispose();
            throw;
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        try
        {
            var read = _decoder.Read(buffer);
            return Account(read, buffer.Length);
        }
        catch (Exception ex) when (IsDecoderFailure(ex))
        {
            throw ImageFailures.Unreadable($"{Format}: {ex.Message}", ex);
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            var read = await _decoder.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            return Account(read, buffer.Length);
        }
        catch (Exception ex) when (IsDecoderFailure(ex))
        {
            throw ImageFailures.Unreadable($"{Format}: {ex.Message}", ex);
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    /// <summary>Counts the output and turns a premature end into an error when the size is known.</summary>
    private int Account(int read, int requested)
    {
        _produced += read;
        if (read == 0 && requested > 0 && UncompressedLength is { } expected && _produced != expected)
        {
            throw ImageFailures.Truncated(expected, _produced);
        }

        return read;
    }

    /// <summary>Decoders throw whatever the damaged data trips over, so this is a list of what counts as "the data is bad".</summary>
    private static bool IsDecoderFailure(Exception ex) =>
        ex is not ObjectDisposedException
        && ex is InvalidDataException or EndOfStreamException or SharpCompressException or ZstdException or NotSupportedException
            or ArgumentException or InvalidOperationException or IndexOutOfRangeException or OverflowException
            or FormatException or InvalidCastException or NullReferenceException;

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _decoder.Dispose();
            _input?.Dispose();
            _archive?.Dispose();
            if (!_leaveOpen)
            {
                _zipSource?.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}
