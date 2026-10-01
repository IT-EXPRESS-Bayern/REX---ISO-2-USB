// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Bootrix.Core.Images;

/// <summary>
/// Case-insensitive catalogue of the files and directories of an image, keyed by normalised path
/// ("efi/boot/bootx64.efi"). Fingerprints are written as glob patterns against it: <c>*</c> and <c>?</c>
/// stay inside one path segment, <c>**</c> spans any number of segments.
/// </summary>
internal sealed partial class ImageFileIndex
{
    private static readonly ConcurrentDictionary<string, Regex> GlobCache = new(StringComparer.Ordinal);

    private readonly Dictionary<string, FileRecord> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="Path">Normalised path.</param>
    /// <param name="Original">Path exactly as the file system reader reported it, needed to open the file again.</param>
    internal readonly record struct FileRecord(string Path, string Original, long Length);

    public int FileCount => _files.Count;

    public int DirectoryCount => _directories.Count;

    public long TotalBytes { get; private set; }

    public long LargestFileBytes { get; private set; }

    /// <summary>The walk stopped at the entry limit or a directory could not be read.</summary>
    public bool Incomplete { get; set; }

    public IEnumerable<FileRecord> Files => _files.Values;

    public IEnumerable<string> Directories => _directories;

    public void AddFile(string path, long length)
    {
        var key = Normalize(path);
        if (key.Length == 0 || !_files.TryAdd(key, new FileRecord(key, path, length)))
        {
            return;
        }

        TotalBytes += length;
        LargestFileBytes = Math.Max(LargestFileBytes, length);
        AddParents(key);
    }

    public void AddDirectory(string path)
    {
        var key = Normalize(path);
        if (key.Length > 0)
        {
            _directories.Add(key);
            AddParents(key);
        }
    }

    private void AddParents(string key)
    {
        for (var slash = key.LastIndexOf('/'); slash > 0; slash = key.LastIndexOf('/', slash - 1))
        {
            if (!_directories.Add(key[..slash]))
            {
                break;
            }
        }
    }

    /// <summary>Entries of the root directory, directories first.</summary>
    public IEnumerable<(string Name, bool IsDirectory)> RootEntries() =>
        _directories.Where(path => !path.Contains('/')).Order(StringComparer.OrdinalIgnoreCase).Select(path => (path, true))
            .Concat(_files.Keys.Where(path => !path.Contains('/')).Order(StringComparer.OrdinalIgnoreCase).Select(path => (path, false)));

    public bool HasFile(string path) => _files.ContainsKey(Normalize(path));

    public bool HasDirectory(string path) => _directories.Contains(Normalize(path));

    public long? LengthOf(string path) => _files.TryGetValue(Normalize(path), out var record) ? record.Length : null;

    /// <summary>The path in the form the file system reader reported it, or null if the file is absent.</summary>
    public string? OriginalPathOf(string path) => _files.TryGetValue(Normalize(path), out var record) ? record.Original : null;

    /// <summary>Files and directories whose path matches the glob pattern.</summary>
    public IEnumerable<string> Find(string glob)
    {
        var regex = GlobCache.GetOrAdd(glob.ToLowerInvariant(), ToRegex);
        return _files.Keys.Concat(_directories).Where(path => regex.IsMatch(path));
    }

    public bool Matches(string glob) => Find(glob).Any();

    public bool MatchesFile(string glob)
    {
        var regex = GlobCache.GetOrAdd(glob.ToLowerInvariant(), ToRegex);
        return _files.Keys.Any(path => regex.IsMatch(path));
    }

    public IEnumerable<string> FindFiles(string glob)
    {
        var regex = GlobCache.GetOrAdd(glob.ToLowerInvariant(), ToRegex);
        return _files.Keys.Where(path => regex.IsMatch(path));
    }

    /// <summary>"\EFI\BOOT\BOOTX64.EFI;1" becomes "EFI/BOOT/BOOTX64.EFI"; ISO 9660 level 1 writes "BOOTMGR." for a name without extension.</summary>
    public static string Normalize(string path)
    {
        var normalised = path.Replace('\\', '/').Trim('/');
        var version = normalised.LastIndexOf(';');
        if (version > normalised.LastIndexOf('/') && VersionSuffix().IsMatch(normalised[version..]))
        {
            normalised = normalised[..version];
        }

        return normalised.TrimEnd('.');
    }

    private static Regex ToRegex(string glob)
    {
        var pattern = new System.Text.StringBuilder("^");
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];
            if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*')
            {
                // "**/" also matches no directory at all, "**" at the end matches the rest.
                if (i + 2 < glob.Length && glob[i + 2] == '/')
                {
                    pattern.Append("(?:.*/)?");
                    i += 2;
                }
                else
                {
                    pattern.Append(".*");
                    i++;
                }
            }
            else if (c == '*')
            {
                pattern.Append("[^/]*");
            }
            else if (c == '?')
            {
                pattern.Append("[^/]");
            }
            else
            {
                pattern.Append(Regex.Escape(c.ToString()));
            }
        }

        pattern.Append('$');
        return new Regex(pattern.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    [GeneratedRegex(@"^;\d+$")]
    private static partial Regex VersionSuffix();
}
