// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Compression;

namespace Bootrix.Core.Images.Compression;

/// <summary>
/// Picks the image out of a zip archive. Download pages ship ISOs next to checksum files and
/// readme texts, and macOS archivers add resource-fork entries, so the choice is made by content type
/// and size rather than by position.
/// </summary>
internal static class ZipEntrySelector
{
    private static readonly string[] SkippedPrefixes = ["__MACOSX/"];

    public static List<ZipArchiveEntry> Candidates(ZipArchive archive) =>
    [
        .. archive.Entries.Where(entry =>
            !entry.FullName.EndsWith('/')
            && !SkippedPrefixes.Any(prefix => entry.FullName.StartsWith(prefix, StringComparison.Ordinal))
            && !entry.Name.StartsWith("._", StringComparison.Ordinal)
            && !entry.Name.Equals(".DS_Store", StringComparison.Ordinal)),
    ];

    public static ZipArchiveEntry Select(IReadOnlyList<ZipArchiveEntry> candidates, string? name)
    {
        if (candidates.Count == 0)
        {
            throw ImageErrors.Unreadable("The zip archive contains no files.");
        }

        if (name is not null)
        {
            return candidates.FirstOrDefault(entry => entry.FullName.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? candidates.FirstOrDefault(entry => entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw ImageErrors.Unreadable($"The zip archive has no entry named '{name}'.");
        }

        var images = candidates.Where(entry => ImageFileTypes.IsKnown(entry.Name)).ToList();
        return (images.Count > 0 ? images : candidates).MaxBy(entry => entry.Length)!;
    }
}
