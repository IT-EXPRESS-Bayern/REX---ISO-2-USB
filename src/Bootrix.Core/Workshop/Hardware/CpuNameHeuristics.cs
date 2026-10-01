// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;

namespace Bootrix.Core.Workshop.Hardware;

/// <summary>
/// Estimates from the processor's marketing name. Microsoft's list of supported processors is the only authority and
/// is not embedded; these rules only recognise generations that are clearly older than it starts (Intel Core before the
/// 8th generation, AMD before Zen+, the NetBurst/Core 2/K10 era) and must be presented as an estimate.
/// </summary>
public static partial class CpuNameHeuristics
{
    private const int FirstSupportedIntelCoreGeneration = 8;

    public static bool LooksUnsupportedByWindows11(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (IntelCoreModel().Match(name) is { Success: true } intel)
        {
            return IntelCoreGeneration(intel.Groups["digits"].Value) < FirstSupportedIntelCoreGeneration;
        }

        // The first Ryzen generation (1000 series) is the only Zen core that predates the supported list.
        if (RyzenModel().Match(name) is { Success: true } ryzen)
        {
            return ryzen.Groups["series"].Value == "1";
        }

        return LegacyFamily().IsMatch(name);
    }

    /// <summary>
    /// Snapdragon X2 and NVIDIA N1X are the platforms Microsoft scoped Windows 11 26H1 to. The chip names come from the
    /// vendors' announcements and have not been confirmed on Microsoft's release page, so callers must treat a match as a hint.
    /// </summary>
    public static bool IsNewArmPlatform(string? name) =>
        !string.IsNullOrWhiteSpace(name) && NewArmPlatform().IsMatch(name);

    /// <summary>
    /// Generation from an Intel Core model number: 3 digits are 1st generation, 5 digits use the first two. Four digits use
    /// the first digit, except the mobile 10th to 13th generation (1065G7, 1165G7, 1260P, 1365U), which start with "1".
    /// </summary>
    private static int IntelCoreGeneration(string digits) => digits.Length switch
    {
        3 => 1,
        4 when digits[0] != '1' => digits[0] - '0',
        _ => int.Parse(digits[..2], CultureInfo.InvariantCulture),
    };

    [GeneratedRegex(@"\bi[3579]-(?<digits>\d{3,5})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex IntelCoreModel();

    [GeneratedRegex(@"\bRyzen\s+[3579]\s+(?:PRO\s+)?(?<series>\d)\d{3}", RegexOptions.IgnoreCase)]
    private static partial Regex RyzenModel();

    [GeneratedRegex(
        @"Core\(TM\)\s?2|Pentium\(R\)\s+(?:4|D|Dual-Core)\b|\bAthlon\(tm\)\s+(?:64|II|X[234])|\bPhenom\b|\bFX\b|\bOpteron\b|\bA(?:4|6|8|10|12)-[3-8]\d{3}|Atom\(TM\) CPU\s+[NDZE]\d{3,4}",
        RegexOptions.IgnoreCase)]
    private static partial Regex LegacyFamily();

    [GeneratedRegex(@"Snapdragon\(R\)?\s+X2|\bX2[EP]?-\d{2}-\d{3}|\bN1X\b", RegexOptions.IgnoreCase)]
    private static partial Regex NewArmPlatform();
}
