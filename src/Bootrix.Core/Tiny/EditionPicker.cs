// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bootrix.Core.Errors;

namespace Bootrix.Core.Tiny;

public static class EditionPicker
{
    /// <summary>
    /// Chooses the edition for a build: the only one there is, one given by its index, or the one
    /// whose name contains the text. Anything ambiguous is an error, never a silent guess.
    /// </summary>
    public static int Pick(IReadOnlyList<InstallEdition> editions, string? wanted)
    {
        if (editions.Count == 0)
        {
            throw new BootrixException(ErrorCode.ImageUnsupported, "no editions found");
        }

        if (string.IsNullOrWhiteSpace(wanted))
        {
            return editions.Count == 1
                ? editions[0].Index
                : throw Invalid("several editions", $"The image holds {editions.Count} editions; choose one by index or name.");
        }

        wanted = wanted.Trim();
        if (int.TryParse(wanted, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && editions.Any(e => e.Index == number))
        {
            return number;
        }

        var matches = editions.Where(e => e.Name.Contains(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count switch
        {
            1 => matches[0].Index,
            0 => throw Invalid($"no edition matches '{wanted}'", $"No edition matches '{wanted}'."),
            _ => throw Invalid($"edition '{wanted}' is ambiguous", $"'{wanted}' matches {matches.Count} editions; use the index."),
        };
    }

    private static BootrixException Invalid(string detail, string message) =>
        new(ErrorCode.InvalidSpec, detail) { Arguments = [message] };
}
