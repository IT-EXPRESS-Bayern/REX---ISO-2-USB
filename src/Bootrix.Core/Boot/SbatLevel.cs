// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bootrix.Core.Errors;

namespace Bootrix.Core.Boot;

public sealed record SbatViolation(string Component, int Generation, int Required);

/// <summary>
/// A SBAT revocation level as shim keeps it in the SbatLevel variable: a "sbat,1,&lt;date&gt;" header followed
/// by the minimum generation per component.
/// </summary>
public sealed class SbatLevel
{
    private readonly Dictionary<string, int> _minimum;

    public SbatLevel(string levelDate, IReadOnlyDictionary<string, int> minimumGenerations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(levelDate);
        ArgumentNullException.ThrowIfNull(minimumGenerations);

        LevelDate = levelDate;
        _minimum = new Dictionary<string, int>(minimumGenerations, StringComparer.Ordinal);
    }

    /// <summary>The date stamp of the level, for example 2025112400.</summary>
    public string LevelDate { get; }

    public IReadOnlyDictionary<string, int> MinimumGenerations => _minimum;

    /// <summary>Parses the text form used in SbatLevel_Variable.txt, one level without blank lines.</summary>
    public static SbatLevel Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        string? date = null;
        var minimum = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split(',');
            if (fields[0] == "sbat" && fields.Length >= 3 && date is null)
            {
                date = fields[2];
                continue;
            }

            if (fields.Length < 2 || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var generation))
            {
                throw new BootrixException(ErrorCode.RevocationDataInvalid, "bad SBAT level line") { Arguments = ["SBAT level line: " + line] };
            }

            minimum[fields[0]] = generation;
        }

        return date is null
            ? throw new BootrixException(ErrorCode.RevocationDataInvalid, "SBAT level has no header") { Arguments = ["SBAT level without header"] }
            : new SbatLevel(date, minimum);
    }

    /// <summary>
    /// Entries of the image whose generation is below the level. Generation 0 is skipped: SBAT.md starts counting
    /// at 1, and Rufus treats it as "no information" as well.
    /// </summary>
    public IReadOnlyList<SbatViolation> FindViolations(IEnumerable<SbatEntry> imageEntries)
    {
        ArgumentNullException.ThrowIfNull(imageEntries);

        var violations = new List<SbatViolation>();
        foreach (var entry in imageEntries)
        {
            if (entry.Generation > 0 && _minimum.TryGetValue(entry.Component, out var required) && entry.Generation < required)
            {
                violations.Add(new SbatViolation(entry.Component, entry.Generation, required));
            }
        }

        return violations;
    }
}
