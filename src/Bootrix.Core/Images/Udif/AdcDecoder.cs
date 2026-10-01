// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images.Apple;

namespace Bootrix.Core.Images.Udif;

/// <summary>
/// Apple Data Compression, the LZ77 variant of UDCO images. Control byte layout:
/// bit 7 set = literal run of (b &amp; 0x7F) + 1 bytes; bit 6 set = 3-byte match, length (b &amp; 0x3F) + 4 and a 16-bit
/// distance; otherwise a 2-byte match, length ((b &amp; 0x3F) >> 2) + 3 and a 10-bit distance. Distances count back
/// from the last written byte minus one, and matches may overlap their own output.
/// </summary>
internal static class AdcDecoder
{
    public static void Decode(ReadOnlySpan<byte> input, Span<byte> output)
    {
        var read = 0;
        var written = 0;
        while (read < input.Length && written < output.Length)
        {
            int control = input[read++];
            if ((control & 0x80) != 0)
            {
                var literal = (control & 0x7F) + 1;
                if (literal > input.Length - read || literal > output.Length - written)
                {
                    throw ImageErrors.Corrupt("ADC literal run exceeds its buffer");
                }

                input.Slice(read, literal).CopyTo(output[written..]);
                read += literal;
                written += literal;
                continue;
            }

            int length;
            int distance;
            if ((control & 0x40) != 0)
            {
                if (input.Length - read < 2)
                {
                    throw ImageErrors.Corrupt("ADC stream ends inside a match");
                }

                length = (control & 0x3F) + 4;
                distance = (input[read] << 8) | input[read + 1];
                read += 2;
            }
            else
            {
                if (input.Length - read < 1)
                {
                    throw ImageErrors.Corrupt("ADC stream ends inside a match");
                }

                length = ((control & 0x3F) >> 2) + 3;
                distance = ((control & 0x03) << 8) | input[read];
                read++;
            }

            var source = written - distance - 1;
            if (source < 0 || length > output.Length - written)
            {
                throw ImageErrors.Corrupt("ADC match points outside the decoded data");
            }

            // Byte-wise on purpose: a distance shorter than the length repeats the pattern.
            for (var i = 0; i < length; i++)
            {
                output[written + i] = output[source + i];
            }

            written += length;
        }

        if (written != output.Length)
        {
            throw ImageErrors.Corrupt("ADC chunk is shorter than its sector count");
        }
    }
}
