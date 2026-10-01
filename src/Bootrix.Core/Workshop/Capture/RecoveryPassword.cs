// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace Bootrix.Core.Workshop.Capture;

/// <summary>
/// The BitLocker numerical recovery password: eight groups of six digits. Each group is the 16-bit value of a key block times 11,
/// which gives every group a checksum: it must be divisible by 11 and below 11 x 65536.
/// </summary>
public static class RecoveryPassword
{
    public const int GroupCount = 8;
    public const int GroupLength = 6;

    private const int GroupDivisor = 11;
    private const int GroupLimit = GroupDivisor * 65536;

    public static bool IsValid(string? password)
    {
        if (password is null)
        {
            return false;
        }

        var groups = password.Split('-');
        if (groups.Length != GroupCount)
        {
            return false;
        }

        foreach (var group in groups)
        {
            if (group.Length != GroupLength
                || !int.TryParse(group, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
                || value % GroupDivisor != 0
                || value >= GroupLimit)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Hides all digits and keeps the shape: XXXXXX-XXXXXX-...-XXXXXX.</summary>
    public static string Mask() => string.Join('-', Enumerable.Repeat(new string('X', GroupLength), GroupCount));
}
