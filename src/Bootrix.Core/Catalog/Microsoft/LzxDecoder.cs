// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace Bootrix.Core.Catalog.Microsoft;

/// <summary>
/// LZX as used in cabinet files (not the DELTA variant of patch files). The folder is cut into 32 KiB frames,
/// one per CFDATA block; the bit stream restarts at every frame while trees, repeated offsets and the block in
/// progress carry over. The caller's output buffer doubles as the window, so matches simply look back into it.
/// </summary>
internal sealed class LzxDecoder
{
    public const int FrameSize = 32768;

    private const int NumChars = 256;
    private const int MinMatch = 2;
    private const int PrimaryLengths = 7;
    private const int LengthSymbols = 249;
    private const int PretreeSymbols = 20;
    private const int AlignedSymbols = 8;

    // Slots 36 and up all carry 17 extra bits; the table stops where the largest window (2 MiB) does.
    private static readonly int[] ExtraBits = Enumerable.Range(0, 51).Select(slot => slot < 4 ? 0 : slot < 36 ? (slot - 2) / 2 : 17).ToArray();
    private static readonly int[] PositionBase = BuildPositionBase();

    private readonly int _windowSize;
    private readonly int _mainSymbols;
    private readonly byte[] _mainLengths;
    private readonly byte[] _lengthLengths = new byte[LengthSymbols];

    private LzxHuffmanTree? _main;
    private LzxHuffmanTree? _length;
    private LzxHuffmanTree? _aligned;
    private BlockType _type;
    private int _blockLength;
    private int _blockRemaining;
    private int _r0 = 1;
    private int _r1 = 1;
    private int _r2 = 1;
    private bool _headerRead;
    private bool _intelStarted;
    private int _intelFileSize;
    private bool _paddingPending;

    public LzxDecoder(int windowBits)
    {
        if (windowBits is < 15 or > 21)
        {
            throw new NotSupportedException($"LZX window of 2^{windowBits} bytes is not supported.");
        }

        _windowSize = 1 << windowBits;
        var slots = windowBits switch { 20 => 42, 21 => 50, _ => windowBits * 2 };
        _mainSymbols = NumChars + slots * 8;
        _mainLengths = new byte[_mainSymbols];
    }

    private enum BlockType
    {
        None = 0,
        Verbatim = 1,
        Aligned = 2,
        Uncompressed = 3,
    }

    /// <param name="input">Compressed payload of one CFDATA block.</param>
    /// <param name="output">The whole folder's output buffer; everything before <paramref name="position"/> is the window.</param>
    /// <param name="position">Where this frame starts in <paramref name="output"/>.</param>
    /// <param name="length">Uncompressed size of the frame, at most <see cref="FrameSize"/>.</param>
    public void DecodeFrame(ReadOnlySpan<byte> input, Span<byte> output, int position, int length)
    {
        if (length is <= 0 or > FrameSize)
        {
            throw new InvalidDataException($"LZX frame of {length} bytes.");
        }

        var reader = new LzxBitReader(input);

        if (_paddingPending)
        {
            reader.SkipByte();
            _paddingPending = false;
        }

        if (!_headerRead)
        {
            ReadStreamHeader(ref reader);
        }

        var end = position + length;
        var current = position;

        while (current < end)
        {
            if (_blockRemaining == 0)
            {
                ReadBlockHeader(ref reader);
            }

            var run = Math.Min(_blockRemaining, end - current);
            _blockRemaining -= run;

            if (_type == BlockType.Uncompressed)
            {
                reader.TakeBytes(run).CopyTo(output[current..]);
                current += run;

                // Raw data is padded to a whole word; the pad byte may sit at the end of this frame or start the next.
                if (_blockRemaining == 0 && (_blockLength & 1) == 1)
                {
                    if (reader.AtEnd)
                    {
                        _paddingPending = true;
                    }
                    else
                    {
                        reader.SkipByte();
                    }
                }
            }
            else
            {
                current = DecodeSymbols(ref reader, output, current, run);
            }
        }

        reader.ThrowIfOverrun();

        var frameIndex = position / FrameSize;
        if (_intelStarted && _intelFileSize != 0 && frameIndex <= 32768)
        {
            TranslateCallTargets(output.Slice(position, length), position);
        }
    }

    private static int[] BuildPositionBase()
    {
        var table = new int[ExtraBits.Length];
        for (var slot = 1; slot < table.Length; slot++)
        {
            table[slot] = table[slot - 1] + (1 << ExtraBits[slot - 1]);
        }

        return table;
    }

    private void ReadStreamHeader(ref LzxBitReader reader)
    {
        _headerRead = true;

        // Optional size for the x86 call-target translation: one flag bit, then two 16-bit halves.
        if (reader.ReadBits(1) == 1)
        {
            var high = reader.ReadBits(16);
            var low = reader.ReadBits(16);
            _intelFileSize = (int)((high << 16) | low);
        }
    }

    private void ReadBlockHeader(ref LzxBitReader reader)
    {
        var type = (BlockType)reader.ReadBits(3);
        var length = (int)((reader.ReadBits(16) << 8) | reader.ReadBits(8));

        switch (type)
        {
            case BlockType.Aligned:
                Span<byte> alignedLengths = stackalloc byte[AlignedSymbols];
                for (var i = 0; i < AlignedSymbols; i++)
                {
                    alignedLengths[i] = (byte)reader.ReadBits(3);
                }

                _aligned = new LzxHuffmanTree(alignedLengths);
                ReadTrees(ref reader);
                break;

            case BlockType.Verbatim:
                ReadTrees(ref reader);
                break;

            case BlockType.Uncompressed:
                _intelStarted = true;
                reader.AlignToWord();
                var repeated = reader.TakeBytes(12);
                _r0 = BinaryPrimitives.ReadInt32LittleEndian(repeated);
                _r1 = BinaryPrimitives.ReadInt32LittleEndian(repeated[4..]);
                _r2 = BinaryPrimitives.ReadInt32LittleEndian(repeated[8..]);
                break;

            default:
                throw new InvalidDataException($"LZX block type {(int)type}.");
        }

        _type = type;
        _blockLength = length;
        _blockRemaining = length;
    }

    private void ReadTrees(ref LzxBitReader reader)
    {
        ReadLengths(ref reader, _mainLengths, 0, NumChars);
        ReadLengths(ref reader, _mainLengths, NumChars, _mainSymbols);
        _main = new LzxHuffmanTree(_mainLengths);

        // An x86 call opcode among the literals means the translation may have been applied.
        if (_mainLengths[0xE8] != 0)
        {
            _intelStarted = true;
        }

        ReadLengths(ref reader, _lengthLengths, 0, LengthSymbols);
        _length = new LzxHuffmanTree(_lengthLengths);
    }

    /// <summary>
    /// Code lengths are sent as differences to the previous block's lengths, run-length coded with a small
    /// Huffman code of their own.
    /// </summary>
    private static void ReadLengths(ref LzxBitReader reader, byte[] lengths, int first, int last)
    {
        Span<byte> pretreeLengths = stackalloc byte[PretreeSymbols];
        for (var i = 0; i < PretreeSymbols; i++)
        {
            pretreeLengths[i] = (byte)reader.ReadBits(4);
        }

        var pretree = new LzxHuffmanTree(pretreeLengths);
        var index = first;

        while (index < last)
        {
            var symbol = pretree.Decode(ref reader);
            int run;
            byte value;

            switch (symbol)
            {
                case 17:
                    run = (int)reader.ReadBits(4) + 4;
                    value = 0;
                    break;
                case 18:
                    run = (int)reader.ReadBits(5) + 20;
                    value = 0;
                    break;
                case 19:
                    run = (int)reader.ReadBits(1) + 4;
                    value = Delta(lengths[index], pretree.Decode(ref reader));
                    break;
                default:
                    run = 1;
                    value = Delta(lengths[index], symbol);
                    break;
            }

            if (index + run > last)
            {
                throw new InvalidDataException("LZX code length run leaves the tree.");
            }

            lengths.AsSpan(index, run).Fill(value);
            index += run;
        }
    }

    private static byte Delta(byte previous, int symbol)
    {
        var length = previous - symbol;
        return (byte)(length < 0 ? length + 17 : length);
    }

    private int DecodeSymbols(ref LzxBitReader reader, Span<byte> output, int current, int run)
    {
        var limit = current + run;
        var aligned = _type == BlockType.Aligned;

        while (current < limit)
        {
            var symbol = _main!.Decode(ref reader);

            if (symbol < NumChars)
            {
                output[current++] = (byte)symbol;
                continue;
            }

            symbol -= NumChars;
            var matchLength = symbol & PrimaryLengths;
            if (matchLength == PrimaryLengths)
            {
                if (_length!.IsEmpty)
                {
                    throw new InvalidDataException("LZX match uses an empty length tree.");
                }

                matchLength += _length.Decode(ref reader);
            }

            matchLength += MinMatch;
            var offset = ReadOffset(ref reader, symbol >> 3, aligned);

            // Frames and blocks are self-contained: no match runs over the end of either.
            if (matchLength > limit - current)
            {
                throw new InvalidDataException("LZX match crosses a frame or block boundary.");
            }

            if (offset < 1 || offset > current || offset > _windowSize)
            {
                throw new InvalidDataException($"LZX match offset {offset} points outside the window.");
            }

            // Source and target may overlap; the copy has to run forward byte by byte.
            for (var i = 0; i < matchLength; i++, current++)
            {
                output[current] = output[current - offset];
            }
        }

        return current;
    }

    private int ReadOffset(ref LzxBitReader reader, int slot, bool aligned)
    {
        int offset;

        switch (slot)
        {
            case 0:
                return _r0;
            case 1:
                offset = _r1;
                _r1 = _r0;
                _r0 = offset;
                return offset;
            case 2:
                offset = _r2;
                _r2 = _r0;
                _r0 = offset;
                return offset;
        }

        var extra = slot >= 36 ? 17 : ExtraBits[slot];
        offset = PositionBase[slot] - 2;

        if (aligned && extra >= 3)
        {
            // The low three bits come from the aligned tree, which is cheaper when the encoder uses them unevenly.
            if (extra > 3)
            {
                offset += (int)reader.ReadBits(extra - 3) << 3;
            }

            offset += _aligned!.Decode(ref reader);
        }
        else
        {
            offset += (int)reader.ReadBits(extra);
        }

        _r2 = _r1;
        _r1 = _r0;
        _r0 = offset;
        return offset;
    }

    /// <summary>Undoes the encoder's rewrite of x86 CALL operands (E8 xx xx xx xx) from relative to absolute addresses.</summary>
    private void TranslateCallTargets(Span<byte> frame, int streamOffset)
    {
        if (frame.Length <= 10)
        {
            return;
        }

        var end = frame.Length - 10;
        var current = streamOffset;
        var index = 0;

        while (index < end)
        {
            if (frame[index++] != 0xE8)
            {
                current++;
                continue;
            }

            var absolute = BinaryPrimitives.ReadInt32LittleEndian(frame[index..]);
            if (absolute >= -current && absolute < _intelFileSize)
            {
                var relative = absolute >= 0 ? absolute - current : absolute + _intelFileSize;
                BinaryPrimitives.WriteInt32LittleEndian(frame[index..], relative);
            }

            index += 4;
            current += 5;
        }
    }
}
