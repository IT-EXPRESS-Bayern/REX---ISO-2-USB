// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Tests.Images;

/// <summary>
/// Reproducible test payloads. The generators use their own xorshift state so the bytes never depend on the
/// runtime; the embedded LZFSE and XZ fixtures were compressed from exactly these streams.
/// </summary>
internal static class ImageTestData
{
    private static readonly string[] Words =
    [
        "bootrix", "volume", "sector", "partition", "journal", "catalog", "extent", "header", "block", "alpha",
        "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet", "kilo", "lima",
    ];

    public static byte[] Random(int length, ulong seed)
    {
        var data = new byte[length];
        var state = seed | 1;
        for (var i = 0; i < data.Length; i++)
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            data[i] = (byte)(state >> 24);
        }

        return data;
    }

    /// <summary>Text-like data that compresses well but is not trivially periodic.</summary>
    public static byte[] Text(int length, ulong seed)
    {
        var data = new byte[length];
        var state = seed | 1;
        var position = 0;
        while (position < length)
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            var word = Words[(int)((state >> 20) % (ulong)Words.Length)];
            foreach (var c in word)
            {
                if (position == length)
                {
                    break;
                }

                data[position++] = (byte)c;
            }

            if (position < length)
            {
                data[position++] = (state & 0x100) != 0 ? (byte)' ' : (byte)'\n';
            }
        }

        return data;
    }

    /// <summary>A repeating pattern with a few edits; matches in later blocks reach back into earlier ones.</summary>
    public static byte[] Periodic(int length, int period, ulong seed)
    {
        var block = Random(period, seed);
        var data = new byte[length];
        for (var i = 0; i < data.Length; i++)
        {
            data[i] = block[i % period];
        }

        for (var i = 4099; i < data.Length; i += 20011)
        {
            data[i] ^= 0x5A;
        }

        return data;
    }

    /// <summary>
    /// A disk-like volume: random, text, zero and periodic regions in sector-aligned runs so every
    /// chunk kind gets exercised.
    /// </summary>
    public static byte[] Volume(int sectors, ulong seed)
    {
        var volume = new byte[sectors * 512];
        var position = 0;
        var kind = 0;
        while (position < volume.Length)
        {
            var run = Math.Min(volume.Length - position, ((int)((seed + (ulong)kind * 7919) % 97) + 3) * 512 * 5);
            var part = (kind % 4) switch
            {
                0 => Text(run, seed + (ulong)kind),
                1 => Random(run, seed + (ulong)kind),
                2 => new byte[run],
                _ => Periodic(run, 1021, seed + (ulong)kind),
            };
            part.CopyTo(volume, position);
            position += run;
            kind++;
        }

        return volume;
    }
}
