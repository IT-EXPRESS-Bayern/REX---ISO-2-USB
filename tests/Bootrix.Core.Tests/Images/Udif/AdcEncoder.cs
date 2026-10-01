// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Tests.Images.Udif;

/// <summary>A small greedy ADC compressor, only used to produce test chunks.</summary>
internal static class AdcEncoder
{
    private const int MaxLiteral = 128;
    private const int MaxShortLength = 18;
    private const int MaxLongLength = 67;
    private const int MaxShortDistance = 1024;
    private const int MaxLongDistance = 65536;

    public static byte[] Encode(byte[] data)
    {
        var output = new List<byte>(data.Length / 2);
        var lastSeen = new Dictionary<int, int>();
        var literalStart = 0;
        var position = 0;

        void FlushLiterals(int end)
        {
            while (literalStart < end)
            {
                var run = Math.Min(MaxLiteral, end - literalStart);
                output.Add((byte)(0x80 | (run - 1)));
                for (var i = 0; i < run; i++)
                {
                    output.Add(data[literalStart + i]);
                }

                literalStart += run;
            }
        }

        while (position < data.Length)
        {
            var length = 0;
            var distance = 0;
            if (position + 3 <= data.Length)
            {
                var key = (data[position] << 16) | (data[position + 1] << 8) | data[position + 2];
                if (lastSeen.TryGetValue(key, out var candidate) && position - candidate <= MaxLongDistance)
                {
                    distance = position - candidate;
                    length = MatchLength(data, candidate, position, MaxLongLength);
                }

                lastSeen[key] = position;
            }

            if (length >= 4 && (distance > MaxShortDistance || length > MaxShortLength))
            {
                FlushLiterals(position);
                output.Add((byte)(0x40 | (length - 4)));
                output.Add((byte)((distance - 1) >> 8));
                output.Add((byte)((distance - 1) & 0xFF));
                position += length;
                literalStart = position;
            }
            else if (length >= 3 && distance <= MaxShortDistance)
            {
                FlushLiterals(position);
                output.Add((byte)(((length - 3) << 2) | ((distance - 1) >> 8)));
                output.Add((byte)((distance - 1) & 0xFF));
                position += length;
                literalStart = position;
            }
            else
            {
                position++;
            }
        }

        FlushLiterals(data.Length);
        return [.. output];
    }

    private static int MatchLength(byte[] data, int earlier, int later, int limit)
    {
        var length = 0;
        while (length < limit && later + length < data.Length && data[earlier + length] == data[later + length])
        {
            length++;
        }

        return length;
    }
}
