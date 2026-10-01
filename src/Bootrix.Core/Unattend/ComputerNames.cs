// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;

namespace Bootrix.Core.Unattend;

public sealed record ComputerNameContext(string? Serial = null, int Counter = 1, DateOnly? Date = null, Random? Random = null);

/// <summary>
/// Expands a computer name pattern such as "PC-{serial}" or "KUNDE-{n}" at the moment a stick is
/// written, so a batch of sticks can hand out different names. Tokens: {rand} four hex digits,
/// {rand6}, {n} counter, {n3} counter with leading zeros, {date} yyMMdd, {serial} the last six
/// characters of the stick serial.
/// </summary>
public static partial class ComputerNames
{
    public static string Resolve(string pattern, ComputerNameContext? context = null)
    {
        context ??= new ComputerNameContext();
        var random = context.Random ?? Random.Shared;
        var date = context.Date ?? DateOnly.FromDateTime(DateTime.Today);

        var result = Token().Replace(pattern, match => match.Groups[1].Value.ToLowerInvariant() switch
        {
            "rand" => random.Next(0x10000).ToString("X4", CultureInfo.InvariantCulture),
            "rand6" => random.Next(0x1000000).ToString("X6", CultureInfo.InvariantCulture),
            "n" => context.Counter.ToString(CultureInfo.InvariantCulture),
            "n3" => context.Counter.ToString("D3", CultureInfo.InvariantCulture),
            "date" => date.ToString("yyMMdd", CultureInfo.InvariantCulture),
            "serial" => LastCharacters(context.Serial, 6),
            _ => match.Value,
        });

        return result.ToUpperInvariant();
    }

    private static string LastCharacters(string? serial, int count)
    {
        var clean = new string((serial ?? "").Where(char.IsAsciiLetterOrDigit).ToArray());
        return clean.Length <= count ? clean : clean[^count..];
    }

    [GeneratedRegex(@"\{([A-Za-z0-9]+)\}")]
    private static partial Regex Token();
}
