// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Images.Compression;

/// <summary>
/// Decoder for the output of Unix <c>compress</c> (.Z). The format packs codes LSB first in groups of
/// eight, so a group is always <c>n_bits</c> bytes long; when the code width grows or the table is
/// cleared the encoder pads the unfinished group and the decoder has to skip that padding.
/// </summary>
internal sealed class LzwStream : Stream
{
    private const int ClearCode = 256;
    private const int FirstCode = 257;
    private const int InitialBits = 9;

    private readonly Stream _source;
    private readonly bool _leaveOpen;
    private readonly bool _blockMode;
    private readonly int _maxBits;
    private readonly int _maxMaxCode;

    private readonly ushort[] _prefix;
    private readonly byte[] _suffix;
    private readonly byte[] _stack;
    private int _stackLength;
    private int _stackPosition;

    // Three spare bytes so a code can always be assembled from a 24-bit window.
    private readonly byte[] _group = new byte[16 + 3];
    private int _groupBits;
    private int _bitPosition;

    private int _bits = InitialBits;
    private int _maxCode;
    private int _freeEntry;
    private int _oldCode = -1;
    private byte _finalChar;
    private bool _clearPending;
    private bool _finished;

    public LzwStream(Stream source, bool leaveOpen = false)
    {
        _source = source;
        _leaveOpen = leaveOpen;

        Span<byte> header = stackalloc byte[3];
        if (source.ReadAtLeast(header, 3, throwOnEndOfStream: false) < 3 || header[0] != 0x1F || header[1] != 0x9D)
        {
            throw new InvalidDataException("Not a compress (.Z) stream.");
        }

        _maxBits = header[2] & 0x1F;
        _blockMode = (header[2] & 0x80) != 0;
        if (_maxBits is < InitialBits or > 16 || (header[2] & 0x60) != 0)
        {
            throw new InvalidDataException("Unsupported compress header flags.");
        }

        _maxMaxCode = 1 << _maxBits;
        _maxCode = MaxCodeFor(InitialBits);
        _prefix = new ushort[_maxMaxCode];
        _suffix = new byte[_maxMaxCode];
        _stack = new byte[_maxMaxCode];
        for (var i = 0; i < 256; i++)
        {
            _suffix[i] = (byte)i;
        }

        _freeEntry = _blockMode ? FirstCode : 256;
    }

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
        var written = 0;
        while (written < buffer.Length)
        {
            if (_stackPosition == _stackLength && !DecodeNext())
            {
                break;
            }

            // The stack holds the string in reverse; it is drained from the top.
            var take = Math.Min(buffer.Length - written, _stackLength - _stackPosition);
            for (var i = 0; i < take; i++)
            {
                buffer[written + i] = _stack[_stackLength - 1 - _stackPosition - i];
            }

            _stackPosition += take;
            written += take;
        }

        return written;
    }

    /// <summary>Decodes one code into the stack; false at the end of the data.</summary>
    private bool DecodeNext()
    {
        while (!_finished)
        {
            var code = ReadCode();
            if (code < 0)
            {
                _finished = true;
                return false;
            }

            if (code == ClearCode && _blockMode)
            {
                _freeEntry = FirstCode;
                _oldCode = -1;
                _clearPending = true;
                continue;
            }

            if (_oldCode < 0)
            {
                // First code of the stream or right after a clear: always a literal and no table entry yet.
                if (code >= 256)
                {
                    throw new InvalidDataException("Corrupt compress stream (first code is not a literal).");
                }

                _finalChar = (byte)code;
                _oldCode = code;
                _stack[0] = _finalChar;
                _stackLength = 1;
                _stackPosition = 0;
                return true;
            }

            var inCode = code;
            var length = 0;
            if (code >= _freeEntry)
            {
                // KwKwK: the code refers to the entry being defined right now.
                if (code > _freeEntry)
                {
                    throw new InvalidDataException("Corrupt compress stream (code beyond table).");
                }

                _stack[length++] = _finalChar;
                code = _oldCode;
            }

            while (code >= 256)
            {
                _stack[length++] = _suffix[code];
                code = _prefix[code];
            }

            _finalChar = _suffix[code];
            _stack[length++] = _finalChar;

            if (_freeEntry < _maxMaxCode)
            {
                _prefix[_freeEntry] = (ushort)_oldCode;
                _suffix[_freeEntry] = _finalChar;
                _freeEntry++;
            }

            _oldCode = inCode;
            _stackLength = length;
            _stackPosition = 0;
            return true;
        }

        return false;
    }

    /// <summary>At the maximum width the code space is exhausted only when the table is full, so no further widening is announced.</summary>
    private int MaxCodeFor(int bits) => bits == _maxBits ? _maxMaxCode : (1 << bits) - 1;

    private int ReadCode()
    {
        if (_clearPending || _bitPosition >= _groupBits || _freeEntry > _maxCode)
        {
            if (_freeEntry > _maxCode)
            {
                _bits++;
                _maxCode = MaxCodeFor(_bits);
            }

            if (_clearPending)
            {
                _bits = InitialBits;
                _maxCode = MaxCodeFor(InitialBits);
                _clearPending = false;
            }

            var read = _source.ReadAtLeast(_group.AsSpan(0, _bits), _bits, throwOnEndOfStream: false);
            if (read <= 0)
            {
                return -1;
            }

            _bitPosition = 0;
            // A trailing partial group only holds the codes that fit completely.
            _groupBits = (read << 3) - (_bits - 1);
            if (_bitPosition >= _groupBits)
            {
                return -1;
            }
        }

        var index = _bitPosition >> 3;
        var window = _group[index] | (_group[index + 1] << 8) | (_group[index + 2] << 16);
        var code = (window >> (_bitPosition & 7)) & ((1 << _bits) - 1);
        _bitPosition += _bits;
        return code;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_leaveOpen)
        {
            _source.Dispose();
        }

        base.Dispose(disposing);
    }
}
