// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace Bootrix.Core.Catalog.Microsoft;

/// <summary>
/// Bit source of LZX: the stream is a sequence of little-endian 16-bit words whose bits are consumed from the
/// most significant end. Past the end of the input it delivers zeros; <see cref="ThrowIfOverrun"/> tells whether
/// that happened.
/// </summary>
internal ref struct LzxBitReader
{
    private readonly ReadOnlySpan<byte> _input;
    private ulong _buffer;
    private int _count;
    private int _position;

    public LzxBitReader(ReadOnlySpan<byte> input)
    {
        _input = input;
    }

    public readonly bool AtEnd => _position >= _input.Length;

    /// <summary>Reads up to 17 bits.</summary>
    public uint ReadBits(int count)
    {
        if (count == 0)
        {
            return 0;
        }

        while (_count < count)
        {
            _buffer = (_buffer << 16) | NextWord();
            _count += 16;
        }

        _count -= count;
        return (uint)((_buffer >> _count) & ((1UL << count) - 1));
    }

    /// <summary>
    /// Drops the rest of the current word before raw bytes follow. A reader that already stands on a word
    /// boundary still drops a whole word: the format always pads by 1 to 16 bits.
    /// </summary>
    public void AlignToWord()
    {
        if (_count == 0)
        {
            _position += 2;
        }

        _count = 0;
        _buffer = 0;
    }

    public ReadOnlySpan<byte> TakeBytes(int count)
    {
        if (count < 0 || _position + count > _input.Length)
        {
            throw new InvalidDataException("LZX data ends inside an uncompressed block.");
        }

        var bytes = _input.Slice(_position, count);
        _position += count;
        return bytes;
    }

    public void SkipByte() => _position++;

    public readonly void ThrowIfOverrun()
    {
        if ((long)_position * 8 - _count > (long)_input.Length * 8)
        {
            throw new InvalidDataException("LZX data ends before the frame is complete.");
        }
    }

    private uint NextWord()
    {
        uint word = 0;
        if (_position + 1 < _input.Length)
        {
            word = BinaryPrimitives.ReadUInt16LittleEndian(_input[_position..]);
        }
        else if (_position < _input.Length)
        {
            word = _input[_position];
        }

        _position += 2;
        return word;
    }
}
