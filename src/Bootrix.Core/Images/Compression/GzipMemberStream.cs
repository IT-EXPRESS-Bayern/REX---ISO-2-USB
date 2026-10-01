// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Hashing;

namespace Bootrix.Core.Images.Compression;

/// <summary>
/// gzip reader (RFC 1952) that checks every member's trailer. <see cref="GZipStream"/> reads concatenated
/// members but reports a file that was cut off in the middle of a member as a normal end of data, which
/// would silently produce a short disk image. Here the header is parsed by hand, the payload goes through a
/// raw <see cref="DeflateStream"/>, and the CRC-32 and length of the decoded member must show up as the
/// trailer behind the deflate data.
/// </summary>
/// <remarks>
/// <see cref="DeflateStream"/> reads ahead and cannot say where its data ended, so the trailer is looked
/// up by value in the bytes around the read position; a coincidental 64-bit match is not a practical concern.
/// </remarks>
internal sealed class GzipMemberStream(SourceStream source) : Stream
{
    private const int TrailerSize = 8;
    private const int ReadAhead = 16 * 1024;

    private DeflateStream? _deflate;
    private readonly Crc32 _crc = new();
    private long _memberLength;
    private bool _finished;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
        {
            return 0;
        }

        while (!_finished)
        {
            if (_deflate is null && !StartMember())
            {
                _finished = true;
                break;
            }

            var read = _deflate!.Read(buffer);
            if (read > 0)
            {
                _crc.Append(buffer[..read]);
                _memberLength += read;
                return read;
            }

            EndMember();
        }

        return 0;
    }

    /// <summary>False when the input is exhausted; zero padding after the last member is accepted.</summary>
    private bool StartMember()
    {
        var first = source.PeekByte();
        if (first < 0)
        {
            return false;
        }

        if (first == 0)
        {
            SkipZeroPadding();
            return false;
        }

        Span<byte> fixedPart = stackalloc byte[10];
        if (source.Read(fixedPart) < fixedPart.Length || fixedPart[0] != 0x1F || fixedPart[1] != 0x8B || fixedPart[2] != 8)
        {
            throw new InvalidDataException("Data after the gzip stream is not another gzip member.");
        }

        var flags = fixedPart[3];
        if ((flags & 0x04) != 0)
        {
            Span<byte> length = stackalloc byte[2];
            source.ReadExactly(length);
            Skip(BinaryPrimitives.ReadUInt16LittleEndian(length));
        }

        if ((flags & 0x08) != 0)
        {
            SkipZeroTerminated();
        }

        if ((flags & 0x10) != 0)
        {
            SkipZeroTerminated();
        }

        if ((flags & 0x02) != 0)
        {
            Skip(2);
        }

        _crc.Reset();
        _memberLength = 0;
        _deflate = new DeflateStream(source, CompressionMode.Decompress, leaveOpen: true);
        return true;
    }

    private void EndMember()
    {
        _deflate!.Dispose();
        _deflate = null;

        Span<byte> expected = stackalloc byte[TrailerSize];
        BinaryPrimitives.WriteUInt32LittleEndian(expected, _crc.GetCurrentHashAsUInt32());
        BinaryPrimitives.WriteUInt32LittleEndian(expected[4..], (uint)_memberLength);

        // Pull in the bytes the inflater may not have asked for yet, then search the window around the position.
        var ahead = new byte[TrailerSize];
        var extra = source.Read(ahead);
        var back = (int)Math.Min(source.Rewindable, ReadAhead + extra);
        source.Rewind(back);
        var window = new byte[back];
        source.ReadExactly(window);

        var found = window.AsSpan().IndexOf(expected);
        if (found < 0)
        {
            throw new InvalidDataException("A gzip member is incomplete or its CRC-32/length trailer does not match the data.");
        }

        source.Rewind(window.Length - found - TrailerSize);
    }

    private void SkipZeroPadding()
    {
        Span<byte> chunk = stackalloc byte[256];
        int read;
        while ((read = source.Read(chunk)) > 0)
        {
            if (chunk[..read].IndexOfAnyExcept((byte)0) >= 0)
            {
                throw new InvalidDataException("Data after the gzip stream is not another gzip member.");
            }
        }
    }

    private void SkipZeroTerminated()
    {
        int b;
        while ((b = source.PeekByte()) >= 0)
        {
            Skip(1);
            if (b == 0)
            {
                return;
            }
        }

        throw new EndOfStreamException("Unexpected end of the gzip header.");
    }

    private void Skip(int count)
    {
        Span<byte> scratch = stackalloc byte[256];
        while (count > 0)
        {
            var take = Math.Min(count, scratch.Length);
            source.ReadExactly(scratch[..take]);
            count -= take;
        }
    }

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
            _deflate?.Dispose();
        }

        base.Dispose(disposing);
    }
}
