// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;

namespace Bootrix.Core.Tests.FileSystems.Ext;

/// <summary>The parts of a dumpe2fs listing the tests compare: header fields and the per-group block locations.</summary>
internal sealed partial class ExtDump
{
    private ExtDump(Dictionary<string, string> header, List<string> groups, long groupFreeBlocks, long groupFreeInodes)
    {
        Header = header;
        Groups = groups;
        GroupFreeBlocks = groupFreeBlocks;
        GroupFreeInodes = groupFreeInodes;
    }

    public IReadOnlyDictionary<string, string> Header { get; }

    /// <summary>Group headings and metadata locations without checksums, flags or free counts.</summary>
    public IReadOnlyList<string> Groups { get; }

    /// <summary>Sum of the per-group free counts, which e2fsck verifies against the bitmaps (the superblock totals it does not).</summary>
    public long GroupFreeBlocks { get; }

    public long GroupFreeInodes { get; }

    public IReadOnlySet<string> Features => Header["Filesystem features"].Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

    public static ExtDump Read(string image)
    {
        var header = new Dictionary<string, string>();
        var groups = new List<string>();
        long freeBlocks = 0;
        long freeInodes = 0;
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
                if (FreeCounts().Match(text) is { Success: true } counts)
                {
                    freeBlocks += long.Parse(counts.Groups[1].Value, CultureInfo.InvariantCulture);
                    freeInodes += long.Parse(counts.Groups[2].Value, CultureInfo.InvariantCulture);
                }
                else if (text.StartsWith("Primary superblock", StringComparison.Ordinal)
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

        return new ExtDump(header, groups, freeBlocks, freeInodes);
    }

    [GeneratedRegex(@" csum 0x[0-9a-f]+| \[[A-Z_, ]+\]")]
    private static partial Regex GroupDecoration();

    [GeneratedRegex(@"^(\d+) free blocks, (\d+) free inodes")]
    private static partial Regex FreeCounts();
}
