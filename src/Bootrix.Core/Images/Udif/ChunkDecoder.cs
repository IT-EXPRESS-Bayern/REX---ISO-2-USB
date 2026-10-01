// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Compression;
using Bootrix.Core.Errors;
using Bootrix.Core.Images.Apple;
using LzfseSharp;
using SharpCompress.Compressors.BZip2;
using SharpCompress.Compressors.Xz;

namespace Bootrix.Core.Images.Udif;

/// <summary>
/// Decompresses single chunks into a buffer whose size comes from the block table. The buffer size is the
/// only thing that bounds the output, so a hostile stream can neither grow memory nor run past the chunk.
/// </summary>
internal static class ChunkDecoder
{
    // Encoders pick dictionaries by preset, not by chunk size (8 MiB is common), but the decoder allocates the whole
    // dictionary up front, so a hostile header could ask for gigabytes.
    private const long MaxXzDictionary = 64L * 1024 * 1024;

    public static void Decode(UdifChunkType type, byte[] input, int inputLength, byte[] output)
    {
        try
        {
            switch (type)
            {
                case UdifChunkType.Adc:
                    AdcDecoder.Decode(input.AsSpan(0, inputLength), output);
                    break;
                case UdifChunkType.Zlib:
                    ReadAll(new ZLibStream(AsStream(input, inputLength), CompressionMode.Decompress), output);
                    break;
                case UdifChunkType.Bzip2:
                    ReadAll(BZip2Stream.Create(AsStream(input, inputLength), SharpCompress.Compressors.CompressionMode.Decompress, decompressConcatenated: false), output);
                    break;
                case UdifChunkType.Xz:
                    XzDictionaryGuard.EnsureDictionaryWithin(input.AsSpan(0, inputLength), MaxXzDictionary);
                    ReadAll(new XZStream(AsStream(input, inputLength)), output);
                    break;
                case UdifChunkType.Lzfse:
                    DecodeLzfse(input, inputLength, output);
                    break;
                default:
                    throw ImageErrors.Unsupported($"chunk type 0x{(uint)type:X8}");
            }
        }
        catch (Exception ex) when (ex is not (BootrixException or OutOfMemoryException or OperationCanceledException))
        {
            // The third-party decoders report damaged input with a variety of exception types.
            throw ImageErrors.Corrupt($"{type} chunk cannot be decompressed ({ex.GetType().Name}: {ex.Message})");
        }
    }

    private static MemoryStream AsStream(byte[] input, int length) => new(input, 0, length, writable: false);

    private static void ReadAll(Stream decoder, byte[] output)
    {
        using (decoder)
        {
            decoder.ReadExactly(output);

            // Encoders end every chunk with the end-of-stream marker; more data means the sector count is wrong.
            if (decoder.ReadByte() >= 0)
            {
                throw ImageErrors.Corrupt("compressed chunk is longer than its sector count");
            }
        }
    }

    private static void DecodeLzfse(byte[] input, int inputLength, byte[] output)
    {
        var sink = new BoundedSink(output);
        LzfseDecoder.Decompress(AsStream(input, inputLength), sink);
        if (sink.Written != output.Length)
        {
            throw ImageErrors.Corrupt("LZFSE chunk is shorter than its sector count");
        }
    }

    /// <summary>Collects decoder output and refuses to grow beyond the expected size.</summary>
    private sealed class BoundedSink(byte[] target) : Stream
    {
        public int Written { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => Written;

        public override long Position
        {
            get => Written;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length > target.Length - Written)
            {
                throw new InvalidDataException("LZFSE chunk is longer than its sector count");
            }

            buffer.CopyTo(target.AsSpan(Written));
            Written += buffer.Length;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
