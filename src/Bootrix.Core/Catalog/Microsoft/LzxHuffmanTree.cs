// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Catalog.Microsoft;

/// <summary>Canonical Huffman code as LZX defines it: shorter codes first, equal lengths in symbol order, at most 16 bits.</summary>
internal sealed class LzxHuffmanTree
{
    private const int MaxLength = 16;

    private readonly int[] _counts = new int[MaxLength + 1];
    private readonly int[] _symbols;

    public LzxHuffmanTree(ReadOnlySpan<byte> lengths)
    {
        _symbols = new int[lengths.Length];

        foreach (var length in lengths)
        {
            if (length > MaxLength)
            {
                throw new InvalidDataException("LZX code length above 16.");
            }

            _counts[length]++;
        }

        IsEmpty = _counts[0] == lengths.Length;
        _counts[0] = 0;

        var left = 1;
        for (var length = 1; length <= MaxLength; length++)
        {
            left = (left << 1) - _counts[length];
            if (left < 0)
            {
                throw new InvalidDataException("Over-subscribed LZX Huffman code.");
            }
        }

        // Incomplete codes are tolerated here; a bit pattern that maps to no symbol fails when it is decoded.
        var offsets = new int[MaxLength + 2];
        for (var length = 1; length <= MaxLength; length++)
        {
            offsets[length + 1] = offsets[length] + _counts[length];
        }

        for (var symbol = 0; symbol < lengths.Length; symbol++)
        {
            if (lengths[symbol] != 0)
            {
                _symbols[offsets[lengths[symbol]]++] = symbol;
            }
        }
    }

    public bool IsEmpty { get; }

    public int Decode(ref LzxBitReader reader)
    {
        var code = 0;
        var first = 0;
        var index = 0;

        for (var length = 1; length <= MaxLength; length++)
        {
            code |= (int)reader.ReadBits(1);
            var count = _counts[length];

            if (code - count < first)
            {
                return _symbols[index + (code - first)];
            }

            index += count;
            first = (first + count) << 1;
            code <<= 1;
        }

        throw new InvalidDataException("Invalid LZX Huffman code.");
    }
}
