// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Writing.Windows.Customization;

internal static class MediaPaths
{
    /// <summary>
    /// Finds an existing file below the medium root whatever its casing. FAT and NTFS do not care, but names copied
    /// from an ISO are often upper case, and the answer has to be the path the file really has.
    /// </summary>
    /// <returns>The path relative to <paramref name="root"/>, or null when there is no such file.</returns>
    public static string? Resolve(string root, params string[] segments) => Find(root, segments, lastIsDirectory: false);

    /// <summary>Like <see cref="Resolve"/>, for a folder.</summary>
    public static string? ResolveDirectory(string root, params string[] segments) => Find(root, segments, lastIsDirectory: true);

    private static string? Find(string root, string[] segments, bool lastIsDirectory)
    {
        var current = root;
        var relative = new List<string>(segments.Length);
        for (var i = 0; i < segments.Length; i++)
        {
            var directory = lastIsDirectory || i < segments.Length - 1;
            var match = FindEntry(current, segments[i], directory);
            if (match is null)
            {
                return null;
            }

            current = Path.Combine(current, match);
            relative.Add(match);
        }

        return Path.Combine([.. relative]);
    }

    private static string? FindEntry(string directory, string name, bool directories)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var entries = directories ? Directory.EnumerateDirectories(directory) : Directory.EnumerateFiles(directory);
        return entries.Select(Path.GetFileName).FirstOrDefault(entry => string.Equals(entry, name, StringComparison.OrdinalIgnoreCase));
    }
}
