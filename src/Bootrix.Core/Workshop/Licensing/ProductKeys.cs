// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Workshop.Licensing;

public static class ProductKeys
{
    /// <summary>The 24 characters a Windows product key is made of; 'N' is added by the Windows 8 encoding as a position marker.</summary>
    public const string Alphabet = "BCDFGHJKMPQRTVWXY2346789";

    public const int GroupCount = 5;
    public const int GroupLength = 5;

    /// <summary>Length of a key including the four dashes.</summary>
    public const int FormattedLength = (GroupCount * GroupLength) + (GroupCount - 1);

    /// <summary>True for XXXXX-XXXXX-XXXXX-XXXXX-XXXXX built from the key alphabet.</summary>
    public static bool IsValidFormat(string? key)
    {
        if (key is null || key.Length != FormattedLength)
        {
            return false;
        }

        for (var i = 0; i < key.Length; i++)
        {
            var isDash = i % (GroupLength + 1) == GroupLength;
            if (isDash ? key[i] != '-' : !IsKeyCharacter(key[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Hides everything but the last group, e.g. XXXXX-XXXXX-XXXXX-XXXXX-ABCDE. Dashes stay so the shape is recognisable;
    /// text too short to be a key is masked completely.
    /// </summary>
    public static string? Mask(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }

        var visible = key.Length > GroupLength ? GroupLength : 0;
        var chars = key.ToCharArray();
        for (var i = 0; i < chars.Length - visible; i++)
        {
            if (chars[i] != '-')
            {
                chars[i] = 'X';
            }
        }

        return new string(chars);
    }

    /// <summary>Inserts the dashes into a 25-character key.</summary>
    internal static string Format(ReadOnlySpan<char> raw)
    {
        var result = new char[FormattedLength];
        var target = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            if (i > 0 && i % GroupLength == 0)
            {
                result[target++] = '-';
            }

            result[target++] = raw[i];
        }

        return new string(result, 0, target);
    }

    private static bool IsKeyCharacter(char c) => c == 'N' || Alphabet.Contains(c, StringComparison.Ordinal);
}
