// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;

namespace Bootrix.Core.Workshop.Capture;

/// <summary>
/// Reads the output of `netsh wlan show profiles`. The labels are translated ("All User Profile", "Profil für alle Benutzer", ...),
/// so the parser does not look for words: a profile is an indented line of the form "label : name".
/// </summary>
public static partial class NetshWlanParser
{
    public static IReadOnlyList<string> ParseProfileNames(string output)
    {
        var names = new List<string>();
        foreach (var line in output.Split('\n'))
        {
            // Headers such as "Profiles on interface Wi-Fi:" start in the first column; entries are indented.
            if (ProfileLine().Match(line.TrimEnd('\r')) is { Success: true } match)
            {
                var name = match.Groups["name"].Value.Trim();
                if (name.Length > 0 && !names.Contains(name, StringComparer.Ordinal))
                {
                    names.Add(name);
                }
            }
        }

        return names;
    }

    // The label never contains a colon, so the first colon ends it; profile names may contain colons.
    [GeneratedRegex(@"^\s+(?<label>[^:\s][^:]*?)\s*:\s(?<name>.+)$")]
    private static partial Regex ProfileLine();
}
