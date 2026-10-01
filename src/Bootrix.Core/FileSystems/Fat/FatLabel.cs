// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;

namespace Bootrix.Core.FileSystems.Fat;

/// <summary>Volume labels are 11 bytes of upper-case OEM text; the rest is folded or replaced with underscores.</summary>
public static class FatLabel
{
    public const int Length = 11;

    /// <summary>What the boot sector says when no label is set; Windows writes this and adds no root directory entry.</summary>
    public const string Unlabeled = "NO NAME";

    private const string ForbiddenCharacters = "*?.,;:/\\|+=<>[]\"";

    // Code page 437 is the DOS default; using it keeps the label stable regardless of the host's OEM page.
    private static readonly Encoding Oem = CodePagesEncodingProvider.Instance.GetEncoding(
        437, new EncoderReplacementFallback("_"), DecoderFallback.ReplacementFallback)!;

    public static string Normalize(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return "";
        }

        var builder = new StringBuilder(label.Length);
        foreach (var ch in label.ToUpperInvariant())
        {
            builder.Append(char.IsControl(ch) || ForbiddenCharacters.Contains(ch, StringComparison.Ordinal) ? '_' : ch);
        }

        var text = builder.ToString();
        if (text.Length > Length)
        {
            text = text[..Length];
        }

        var bytes = Oem.GetBytes(text);
        if (bytes.Length > 0 && bytes[0] == 0xE5)
        {
            // 0xE5 in the first byte marks a deleted directory entry.
            bytes[0] = (byte)'_';
        }

        return Oem.GetString(bytes).TrimEnd();
    }

    /// <summary>The 11-byte space-padded field as stored in the boot sector and the root directory.</summary>
    public static byte[] ToField(string normalizedLabel)
    {
        var field = new byte[Length];
        Array.Fill(field, (byte)' ');
        Oem.GetBytes(normalizedLabel.AsSpan(0, Math.Min(normalizedLabel.Length, Length)), field);
        return field;
    }
}
