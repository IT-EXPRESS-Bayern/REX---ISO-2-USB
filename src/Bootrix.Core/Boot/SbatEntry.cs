// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace Bootrix.Core.Boot;

/// <summary>
/// One line of the .sbat section (shim SBAT.md): component_name,component_generation,vendor_name,
/// vendor_package_name,vendor_version,vendor_url.
/// </summary>
public sealed record SbatEntry(string Component, int Generation, string? VendorName = null)
{
    private const int MaxSectionBytes = 64 * 1024;
    private const int MaxEntries = 256;

    /// <summary>
    /// Parses the NUL-terminated CSV of a .sbat section. Lines that do not have a component name and a numeric
    /// generation are skipped, which is also how shim treats an unusable image: it simply has no entry to check.
    /// </summary>
    public static IReadOnlyList<SbatEntry> ParseSection(ReadOnlySpan<byte> data)
    {
        var terminator = data.IndexOf((byte)0);
        if (terminator >= 0)
        {
            data = data[..terminator];
        }

        if (data.Length > MaxSectionBytes)
        {
            data = data[..MaxSectionBytes];
        }

        var entries = new List<SbatEntry>();
        foreach (var rawLine in Encoding.UTF8.GetString(data).Split('\n'))
        {
            var fields = rawLine.Trim().Split(',', 4);
            if (fields.Length < 2
                || fields[0].Length == 0
                || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var generation))
            {
                continue;
            }

            entries.Add(new SbatEntry(fields[0], generation, fields.Length > 2 ? fields[2] : null));
            if (entries.Count >= MaxEntries)
            {
                break;
            }
        }

        return entries;
    }
}
