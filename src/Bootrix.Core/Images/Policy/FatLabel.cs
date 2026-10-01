// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;

namespace Bootrix.Core.Images.Policy;

/// <summary>
/// The volume label a FAT file system can carry. Most labels of ISO images (mixed case, dots, longer than 11
/// characters) do not survive, and boot configurations that look the medium up by label then have to be patched.
/// The rules are those Rufus applies in <c>ToValidLabel()</c>.
/// </summary>
internal static class FatLabel
{
    private const string Forbidden = "*?,;:/\\|+=<>[]\"";
    private const int MaxLength = 11;

    public static string ToValid(string label)
    {
        var result = new StringBuilder();
        foreach (var c in label)
        {
            if (Forbidden.Contains(c))
            {
                continue;
            }

            result.Append(c >= 0x80 || c is '.' or '\t' ? '_' : char.ToUpperInvariant(c));
        }

        return result.Length > MaxLength ? result.ToString(0, MaxLength) : result.ToString();
    }

    public static bool WouldChange(string? label) => !string.IsNullOrEmpty(label) && ToValid(label) != label;
}
