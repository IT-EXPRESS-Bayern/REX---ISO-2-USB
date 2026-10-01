// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace Bootrix.Core.Tests.Catalog.Microsoft;

public enum LzxBlockKind
{
    Verbatim = 1,
    Aligned = 2,
    Uncompressed = 3,
}

/// <summary>What the encoder actually produced, so that tests can insist on having covered a code path.</summary>
internal sealed class LzxEncoderStatistics
{
    public int Literals { get; set; }

    public int Matches { get; set; }

    public int RepeatedOffsetMatches { get; set; }

    /// <summary>Matches whose length needed the length tree.</summary>
    public int LongMatches { get; set; }

    /// <summary>Offsets sent partly through the aligned tree.</summary>
    public int AlignedOffsets { get; set; }

    /// <summary>Matches in slot 36 or above, which carry 17 extra bits.</summary>
    public int FarMatches { get; set; }
}

/// <summary>
/// A small greedy LZX encoder, written from the format description and kept apart from the decoder in the product
/// code. It produces what the real fixtures cannot: aligned-offset blocks, uncompressed blocks between coded ones,
/// repeated-offset codes, matches beyond slot 36 and trees that are sent as differences to the previous block's.
/// Its output is checked against the decoder under test and against 7-Zip's.
/// </summary>
internal sealed class LzxTestEncoder
{
    private const int FrameSize = 32768;
    private const int MaxMatch = 257;
    private const int Lookback = 24;

    private readonly int _windowSize;
    private readonly int _slots;
    private readonly int _mainSymbols;
    private readonly bool _paddingStartsNextFrame;
    private readonly int[] _extra = Enumerable.Range(0, 51).Select(s => s < 4 ? 0 : s < 36 ? (s - 2) / 2 : 17).ToArray();
    private readonly int[] _base = new int[51];
    private readonly byte[] _mainLengths;
    private readonly byte[] _lengthLengths = new byte[249];
    private readonly Dictionary<int, List<int>> _index = [];
    private readonly List<byte[]> _frames = [];

    private byte[] _data = [];
    private LzxFrameWriter _writer = new();
    private int _indexed;
    private int _r0 = 1;
    private int _r1 = 1;
    private int _r2 = 1;

    /// <param name="paddingStartsNextFrame">
    /// Where the pad byte of an odd-sized uncompressed block goes when the block ends exactly at the end of a frame:
    /// at the end of that frame (false) or at the start of the next (true).
    /// </param>
    public LzxTestEncoder(int windowBits, bool paddingStartsNextFrame = false)
    {
        _windowSize = 1 << windowBits;
        _slots = windowBits switch { 20 => 42, 21 => 50, _ => windowBits * 2 };
        _mainSymbols = 256 + _slots * 8;
        _mainLengths = new byte[_mainSymbols];
        _paddingStartsNextFrame = paddingStartsNextFrame;

        for (var slot = 1; slot < _base.Length; slot++)
        {
            _base[slot] = _base[slot - 1] + (1 << _extra[slot - 1]);
        }
    }

    public LzxEncoderStatistics Statistics { get; } = new();

    /// <summary>One compressed payload per 32 KiB frame, as the CFDATA blocks of a cabinet folder.</summary>
    public IReadOnlyList<(byte[] Payload, int Uncompressed)> Encode(byte[] data, params (LzxBlockKind Kind, int Length)[] blocks)
    {
        if (blocks.Sum(b => b.Length) != data.Length)
        {
            throw new ArgumentException("The blocks must cover the data exactly.", nameof(blocks));
        }

        _data = data;
        _writer = new LzxFrameWriter().StreamHeader();

        var position = 0;
        foreach (var (kind, length) in blocks)
        {
            if (kind == LzxBlockKind.Uncompressed)
            {
                WriteUncompressed(position, length);
            }
            else
            {
                WriteCoded(kind, position, length);
            }

            position += length;
        }

        _frames.Add(_writer.PadToWord().ToArray());
        return [.. _frames.Select((payload, i) => (payload, Math.Min(FrameSize, data.Length - i * FrameSize)))];
    }

    private void EndFrame()
    {
        _frames.Add(_writer.PadToWord().ToArray());
        _writer = new LzxFrameWriter();
    }

    private bool AtFrameEnd(int position) => position % FrameSize == 0 && position < _data.Length;

    private void WriteUncompressed(int start, int length)
    {
        _writer.UncompressedBlock(length, _r0, _r1, _r2);

        var end = start + length;
        var position = start;

        while (position < end)
        {
            var chunk = Math.Min(end - position, FrameSize - position % FrameSize);
            _writer.Raw(_data.AsSpan(position, chunk));
            position += chunk;

            if (position < end && AtFrameEnd(position))
            {
                EndFrame();
            }
        }

        if ((length & 1) == 0)
        {
            if (AtFrameEnd(position))
            {
                EndFrame();
            }

            return;
        }

        if (!AtFrameEnd(position))
        {
            _writer.Pad();
        }
        else if (_paddingStartsNextFrame)
        {
            EndFrame();
            _writer.Pad();
        }
        else
        {
            _writer.Pad();
            EndFrame();
        }
    }

    private void WriteCoded(LzxBlockKind kind, int start, int length)
    {
        var aligned = kind == LzxBlockKind.Aligned;
        var tokens = Tokenize(start, length);

        var mainFrequency = new int[_mainSymbols];
        var lengthFrequency = new int[249];
        var alignedFrequency = new int[8];
        foreach (var token in tokens)
        {
            mainFrequency[token.Symbol]++;
            if (token.LengthFooter >= 0)
            {
                lengthFrequency[token.LengthFooter]++;
            }

            if (aligned && token.ExtraBits >= 3)
            {
                alignedFrequency[token.Footer & 7]++;
                Statistics.AlignedOffsets++;
            }
        }

        var main = HuffmanLengths(mainFrequency, 16);
        var lengths = HuffmanLengths(lengthFrequency, 16);
        var alignedLengths = alignedFrequency.All(f => f == 0) ? Enumerable.Repeat((byte)3, 8).ToArray() : HuffmanLengths(alignedFrequency, 7);

        _writer.Bits((uint)kind, 3);
        _writer.Bits((uint)length >> 8, 16);
        _writer.Bits((uint)length & 0xFF, 8);

        if (aligned)
        {
            foreach (var l in alignedLengths)
            {
                _writer.Bits(l, 3);
            }
        }

        WriteLengths(_mainLengths, main, 0, 256);
        WriteLengths(_mainLengths, main, 256, _mainSymbols);
        WriteLengths(_lengthLengths, lengths, 0, 249);
        main.CopyTo(_mainLengths, 0);
        lengths.CopyTo(_lengthLengths, 0);

        var mainCodes = Codes(main);
        var lengthCodes = Codes(lengths);
        var alignedCodes = Codes(alignedLengths);
        var position = start;

        foreach (var token in tokens)
        {
            _writer.Bits(mainCodes[token.Symbol], main[token.Symbol]);

            if (token.LengthFooter >= 0)
            {
                _writer.Bits(lengthCodes[token.LengthFooter], lengths[token.LengthFooter]);
            }

            if (token.ExtraBits > 0)
            {
                if (aligned && token.ExtraBits >= 3)
                {
                    if (token.ExtraBits > 3)
                    {
                        _writer.Bits((uint)token.Footer >> 3, token.ExtraBits - 3);
                    }

                    _writer.Bits(alignedCodes[token.Footer & 7], alignedLengths[token.Footer & 7]);
                }
                else
                {
                    _writer.Bits((uint)token.Footer, token.ExtraBits);
                }
            }

            position += token.Size;
            if (AtFrameEnd(position))
            {
                EndFrame();
            }
        }
    }

    private List<Token> Tokenize(int start, int length)
    {
        var tokens = new List<Token>();
        var end = start + length;
        var position = start;
        var maxOffset = _windowSize - 3;

        while (position < end)
        {
            IndexUpTo(position);

            // A match never runs over the end of a frame or of the block.
            var limit = Math.Min(MaxMatch, Math.Min(end - position, FrameSize - position % FrameSize));
            var best = (Length: 0, Offset: 0, Repeat: -1);

            int[] repeated = [_r0, _r1, _r2];
            for (var r = 0; r < 3; r++)
            {
                var offset = repeated[r];
                if (offset <= position && offset <= maxOffset)
                {
                    var found = MatchLength(position, offset, limit);
                    if (found >= 2 && found > best.Length)
                    {
                        best = (found, offset, r);
                    }
                }
            }

            if (limit >= 3 && _index.TryGetValue(Key(position), out var candidates))
            {
                for (var i = candidates.Count - 1; i >= 0 && i >= candidates.Count - Lookback; i--)
                {
                    var offset = position - candidates[i];
                    var found = MatchLength(position, offset, limit);
                    if (offset <= maxOffset && found >= 3 && found > best.Length)
                    {
                        best = (found, offset, -1);
                    }
                }
            }

            if (best.Length >= 2)
            {
                tokens.Add(MatchToken(best.Length, best.Offset, best.Repeat));
                position += best.Length;
            }
            else
            {
                tokens.Add(new Token(_data[position], -1, 0, 0, 1));
                Statistics.Literals++;
                position++;
            }
        }

        return tokens;
    }

    private Token MatchToken(int length, int offset, int repeat)
    {
        int slot, extra = 0, footer = 0;

        if (repeat >= 0)
        {
            slot = repeat;
            (_r0, _r1, _r2) = repeat switch
            {
                0 => (_r0, _r1, _r2),
                1 => (_r1, _r0, _r2),
                _ => (_r2, _r1, _r0),
            };
        }
        else
        {
            // Offsets are sent as offset + 2; slots 0 to 2 are the repeated offsets.
            var formatted = offset + 2;
            slot = Array.FindLastIndex(_base, b => b <= formatted);
            if (slot >= _slots)
            {
                throw new InvalidOperationException("Offset outside the window.");
            }

            extra = _extra[slot];
            footer = formatted - _base[slot];
            (_r0, _r1, _r2) = (offset, _r0, _r1);
        }

        var header = Math.Min(length - 2, 7);
        Statistics.Matches++;
        Statistics.RepeatedOffsetMatches += repeat >= 0 ? 1 : 0;
        Statistics.LongMatches += header == 7 ? 1 : 0;
        Statistics.FarMatches += slot >= 36 ? 1 : 0;
        return new Token(256 + (slot << 3) + header, header == 7 ? length - 9 : -1, extra, footer, length);
    }

    private int MatchLength(int position, int offset, int limit)
    {
        var length = 0;
        while (length < limit && _data[position + length] == _data[position + length - offset])
        {
            length++;
        }

        return length;
    }

    private int Key(int position) => _data[position] | (_data[position + 1] << 8) | (_data[position + 2] << 16);

    private void IndexUpTo(int position)
    {
        for (; _indexed < position; _indexed++)
        {
            if (_indexed + 2 < _data.Length)
            {
                var key = Key(_indexed);
                if (!_index.TryGetValue(key, out var list))
                {
                    _index[key] = list = [];
                }

                list.Add(_indexed);
            }
        }
    }

    /// <summary>Sends the code lengths as differences to the previous ones, with runs, behind a small code of their own.</summary>
    private void WriteLengths(byte[] previous, byte[] current, int first, int last)
    {
        var items = new List<(int Symbol, int Extra, int ExtraBits, int Delta)>();

        for (var i = first; i < last;)
        {
            var value = current[i];
            var run = 1;
            while (i + run < last && current[i + run] == value)
            {
                run++;
            }

            if (value == 0 && run >= 20)
            {
                var n = Math.Min(run, 51);
                items.Add((18, n - 20, 5, -1));
                i += n;
            }
            else if (value == 0 && run >= 4)
            {
                var n = Math.Min(run, 19);
                items.Add((17, n - 4, 4, -1));
                i += n;
            }
            else if (run >= 4)
            {
                var n = Math.Min(run, 5);
                items.Add((19, n - 4, 1, Delta(previous[i], value)));
                i += n;
            }
            else
            {
                items.Add((Delta(previous[i], value), 0, 0, -1));
                i++;
            }
        }

        var frequency = new int[20];
        foreach (var item in items)
        {
            frequency[item.Symbol]++;
            if (item.Delta >= 0)
            {
                frequency[item.Delta]++;
            }
        }

        var pretree = HuffmanLengths(frequency, 15);
        var codes = Codes(pretree);
        foreach (var l in pretree)
        {
            _writer.Bits(l, 4);
        }

        foreach (var item in items)
        {
            _writer.Bits(codes[item.Symbol], pretree[item.Symbol]);
            if (item.ExtraBits > 0)
            {
                _writer.Bits((uint)item.Extra, item.ExtraBits);
            }

            if (item.Delta >= 0)
            {
                _writer.Bits(codes[item.Delta], pretree[item.Delta]);
            }
        }
    }

    private static int Delta(int previous, int value) => ((previous - value) % 17 + 17) % 17;

    /// <summary>Canonical code: shorter codes first, equal lengths in symbol order.</summary>
    private static uint[] Codes(byte[] lengths)
    {
        var codes = new uint[lengths.Length];
        uint code = 0;

        for (var length = 1; length <= 16; length++)
        {
            for (var symbol = 0; symbol < lengths.Length; symbol++)
            {
                if (lengths[symbol] == length)
                {
                    codes[symbol] = code++;
                }
            }

            code <<= 1;
        }

        return codes;
    }

    /// <summary>
    /// Huffman lengths for the used symbols, flattened until none exceeds the limit. A single used symbol gets a
    /// partner so that the code stays complete.
    /// </summary>
    private static byte[] HuffmanLengths(int[] frequencies, int maxLength)
    {
        var frequency = (int[])frequencies.Clone();
        var used = frequency.Count(f => f > 0);
        if (used == 0)
        {
            return new byte[frequency.Length];
        }

        if (used == 1)
        {
            frequency[Array.FindIndex(frequency, f => f == 0)] = 1;
        }

        while (true)
        {
            var lengths = BuildLengths(frequency);
            if (lengths.Max() <= maxLength)
            {
                return lengths;
            }

            for (var i = 0; i < frequency.Length; i++)
            {
                if (frequency[i] > 0)
                {
                    frequency[i] = (frequency[i] + 1) / 2;
                }
            }
        }
    }

    private static byte[] BuildLengths(int[] frequency)
    {
        var leaves = frequency.Length;
        var parent = new List<int>(new int[leaves]);
        var queue = new PriorityQueue<int, (long Weight, int Order)>();

        for (var i = 0; i < leaves; i++)
        {
            parent[i] = -1;
            if (frequency[i] > 0)
            {
                queue.Enqueue(i, (frequency[i], i));
            }
        }

        var weights = frequency.Select(f => (long)f).ToList();
        while (queue.Count > 1)
        {
            queue.TryDequeue(out var a, out var weightA);
            queue.TryDequeue(out var b, out var weightB);
            var node = parent.Count;
            parent.Add(-1);
            weights.Add(weightA.Weight + weightB.Weight);
            parent[a] = node;
            parent[b] = node;
            queue.Enqueue(node, (weights[node], node));
        }

        var lengths = new byte[leaves];
        for (var i = 0; i < leaves; i++)
        {
            if (frequency[i] == 0)
            {
                continue;
            }

            var depth = 0;
            for (var n = i; parent[n] >= 0; n = parent[n])
            {
                depth++;
            }

            lengths[i] = (byte)depth;
        }

        return lengths;
    }

    private readonly record struct Token(int Symbol, int LengthFooter, int ExtraBits, int Footer, int Size);
}
