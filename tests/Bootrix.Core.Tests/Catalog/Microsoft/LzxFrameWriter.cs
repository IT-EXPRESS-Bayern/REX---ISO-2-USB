// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace Bootrix.Core.Tests.Catalog.Microsoft;

/// <summary>
/// Writes the compressed payload of one LZX frame by hand. Only uncompressed blocks are produced: they carry the
/// framing rules (word alignment, repeated-offset header, padding, frame carry-over) without needing an encoder.
/// </summary>
internal sealed class LzxFrameWriter
{
    private readonly List<byte> _bytes = [];
    private int _word;
    private int _bitCount;

    public LzxFrameWriter StreamHeader(int? callTranslationSize = null)
    {
        Bits(callTranslationSize is null ? 0u : 1u, 1);
        if (callTranslationSize is { } size)
        {
            Bits((uint)size >> 16, 16);
            Bits((uint)size & 0xFFFF, 16);
        }

        return this;
    }

    public LzxFrameWriter UncompressedBlock(int length, int r0 = 1, int r1 = 1, int r2 = 1)
    {
        BlockHeader(3, length);

        Span<byte> repeated = stackalloc byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(repeated, r0);
        BinaryPrimitives.WriteInt32LittleEndian(repeated[4..], r1);
        BinaryPrimitives.WriteInt32LittleEndian(repeated[8..], r2);
        return Raw(repeated);
    }

    /// <summary>Type and 24-bit length only, followed by the alignment of an uncompressed block.</summary>
    public LzxFrameWriter BlockHeader(uint type, int length)
    {
        Bits(type, 3);
        Bits((uint)length >> 8, 16);
        Bits((uint)length & 0xFF, 8);
        return AlignToWord();
    }

    public LzxFrameWriter Raw(ReadOnlySpan<byte> data)
    {
        if (_bitCount != 0)
        {
            throw new InvalidOperationException("Raw bytes need a word boundary.");
        }

        _bytes.AddRange(data.ToArray());
        return this;
    }

    public LzxFrameWriter Pad()
    {
        _bytes.Add(0);
        return this;
    }

    /// <summary>The format pads with 1 to 16 bits: a writer that already stands on a boundary adds a whole word.</summary>
    public LzxFrameWriter AlignToWord()
    {
        if (_bitCount == 0)
        {
            Bits(0, 16);
            return this;
        }

        while (_bitCount != 0)
        {
            Bits(0, 1);
        }

        return this;
    }

    public byte[] ToArray() => [.. _bytes];

    private void Bits(uint value, int count)
    {
        for (var i = count - 1; i >= 0; i--)
        {
            _word = (_word << 1) | (int)((value >> i) & 1);
            if (++_bitCount == 16)
            {
                _bytes.Add((byte)(_word & 0xFF));
                _bytes.Add((byte)(_word >> 8));
                _word = 0;
                _bitCount = 0;
            }
        }
    }
}
