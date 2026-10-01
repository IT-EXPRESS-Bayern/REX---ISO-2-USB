// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;

namespace Bootrix.Core.Tests.FileSystems.Ext;

/// <summary>The parts of a dumpe2fs listing the tests compare: header fields and the per-group block locations.</summary>
internal sealed partial class ExtDump
{
    private ExtDump(Dictionary<string, string> header, List<string> groups)
    {
        Header = header;
        Groups = groups;
    }

    public IReadOnlyDictionary<string, string> Header { get; }

    /// <summary>Group headings and metadata locations without checksums, flags or free counts.</summary>
    public IReadOnlyList<string> Groups { get; }

    public IReadOnlySet<string> Features => Header["Filesystem features"].Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

    public static ExtDump Read(string image)
    {
        var header = new Dictionary<string, string>();
        var groups = new List<string>();
        var inGroups = false;
        foreach (var line in ExtTools.Run("dumpe2fs", image).Output.Split('\n'))
        {
            if (line.StartsWith("Group ", StringComparison.Ordinal))
            {
                inGroups = true;
                groups.Add(GroupDecoration().Replace(line.TrimEnd(), string.Empty));
            }
            else if (inGroups)
            {
                var text = line.Trim();
                if (text.StartsWith("Primary superblock", StringComparison.Ordinal)
                    || text.StartsWith("Backup superblock", StringComparison.Ordinal)
                    || text.StartsWith("Block bitmap at", StringComparison.Ordinal)
                    || text.StartsWith("Inode bitmap at", StringComparison.Ordinal)
                    || text.StartsWith("Inode table at", StringComparison.Ordinal))
                {
                    groups.Add(text);
                }
            }
            else if (line.IndexOf(':') is var colon and > 0)
            {
                header[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        return new ExtDump(header, groups);
    }

    [GeneratedRegex(@" csum 0x[0-9a-f]+| \[[A-Z_, ]+\]")]
    private static partial Regex GroupDecoration();
}
