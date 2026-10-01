// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Workshop.Licensing;

/// <summary>
/// Decodes the DigitalProductId value of HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion. Bytes 52 to 66 hold the key as a
/// 15-byte little-endian number in base 24 over <see cref="ProductKeys.Alphabet"/>.
/// </summary>
public static class DigitalProductIdDecoder
{
    private const int KeyOffset = 52;
    private const int KeyBytes = 15;
    private const int KeyChars = 25;
    private const byte Windows8Flag = 0x08;

    /// <summary>Returns the key, or null when the blob is too short or does not decode to a well-formed key.</summary>
    public static string? Decode(ReadOnlySpan<byte> digitalProductId)
    {
        if (digitalProductId.Length < KeyOffset + KeyBytes)
        {
            return null;
        }

        Span<byte> number = stackalloc byte[KeyBytes];
        digitalProductId.Slice(KeyOffset, KeyBytes).CopyTo(number);

        // Machines without a stored key leave the area zeroed; that would decode to a key of all B.
        if (!number.ContainsAnyExcept((byte)0))
        {
            return null;
        }

        // Windows 8 and later keep a flag in bit 3 of the most significant byte and move the 'N' out of the number
        // into the first digit. The key itself needs about 115 bits, so the flag does not collide with key data.
        var windows8 = (number[KeyBytes - 1] & Windows8Flag) != 0;
        number[KeyBytes - 1] &= unchecked((byte)~Windows8Flag);

        Span<char> chars = stackalloc char[KeyChars];
        for (var i = KeyChars - 1; i >= 0; i--)
        {
            var remainder = 0;
            for (var j = KeyBytes - 1; j >= 0; j--)
            {
                var current = (remainder * 256) + number[j];
                number[j] = (byte)(current / ProductKeys.Alphabet.Length);
                remainder = current % ProductKeys.Alphabet.Length;
            }

            chars[i] = ProductKeys.Alphabet[remainder];
        }

        var key = windows8 ? InsertMarker(chars) : new string(chars);
        var formatted = ProductKeys.Format(key);
        return ProductKeys.IsValidFormat(formatted) ? formatted : null;
    }

    private static string InsertMarker(ReadOnlySpan<char> chars)
    {
        var position = ProductKeys.Alphabet.IndexOf(chars[0], StringComparison.Ordinal);
        var rest = chars[1..];
        return string.Concat(rest[..position], "N", rest[position..]);
    }
}
