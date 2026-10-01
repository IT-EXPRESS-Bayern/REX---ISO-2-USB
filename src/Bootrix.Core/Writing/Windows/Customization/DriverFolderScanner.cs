// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bootrix.Core.Errors;
using Bootrix.Core.Localization;

namespace Bootrix.Core.Writing.Windows.Customization;

public sealed record DriverLimits
{
    public int MaxFiles { get; init; } = 20_000;

    public long MaxBytes { get; init; } = 1L << 30;

    public int MaxDepth { get; init; } = 12;

    /// <summary>
    /// Longest path of a file below its driver folder. The medium adds "$WinPEDriver$\NN-name\" in front, and
    /// Windows Setup runs in WinPE, where a path above 260 characters cannot be opened.
    /// </summary>
    public int MaxRelativePathLength { get; init; } = 190;
}

public sealed record DriverFile(string RelativePath, long Length)
{
    public bool IsInf => RelativePath.EndsWith(".inf", StringComparison.OrdinalIgnoreCase);
}

/// <param name="Skipped">Files that are not copied, counted by extension: programs and scripts do not belong on a driver path.</param>
public sealed record DriverFolderListing(string Root, IReadOnlyList<DriverFile> Files, IReadOnlyDictionary<string, int> Skipped)
{
    public long Bytes => Files.Sum(file => file.Length);
}

/// <summary>
/// Looks through a folder the user named as a driver source before anything is copied from it. The folder is
/// untrusted input: it may be a drive root, hold links that lead out of it, programs, absurd name lengths or far
/// more data than a driver path needs. Anything that is not a plain driver file is either left out or makes the
/// whole folder unusable; nothing is followed and nothing is guessed.
/// </summary>
public static class DriverFolderScanner
{
    /// <summary>
    /// What a driver package consists of. Programs, installers and scripts are not in the list:
    /// Setup only needs the INF and the files it names, and an executable on the medium is a risk, not a driver.
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".inf", ".sys", ".cat", ".dll", ".bin", ".mui", ".fw", ".dat", ".cfg", ".ini", ".xml", ".json", ".txt",
    };

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private const string InvalidNameCharacters = "<>:\"/\\|?*";

    private const int MaxNameLength = 255;

    private static readonly EnumerationOptions Everything = new()
    {
        // The default skips hidden and system entries, which is exactly where a link would be put.
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
        RecurseSubdirectories = false,
    };

    /// <exception cref="BootrixException">With <see cref="ErrorCode.DriverFolderRejected"/> when the folder cannot be used.</exception>
    public static DriverFolderListing Scan(string folder, DriverLimits? limits = null, CancellationToken cancellationToken = default)
    {
        limits ??= new DriverLimits();
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
        {
            throw Reject(folder ?? "", "Driver.Reject.NotFound");
        }

        // The job's validation refuses ".." already; a folder that still has it is not what the user picked.
        if (folder.Split('\\', '/').Contains(".."))
        {
            throw Reject(folder, "Driver.Reject.BadName", folder);
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var rootInfo = new DirectoryInfo(root);
        if (!rootInfo.Exists)
        {
            throw Reject(root, "Driver.Reject.NotFound");
        }

        // "C:\" as a driver folder would mean copying whatever the user happens to have on the drive.
        if (string.Equals(Path.GetPathRoot(root), root, StringComparison.OrdinalIgnoreCase))
        {
            throw Reject(root, "Driver.Reject.Root");
        }

        if (IsLink(rootInfo))
        {
            throw Reject(root, "Driver.Reject.Link", Path.GetFileName(root));
        }

        var state = new ScanState(root, limits);
        Walk(rootInfo, "", 0, state, cancellationToken);

        if (!state.Files.Any(file => file.IsInf))
        {
            throw Reject(root, "Driver.Reject.NoDriver");
        }

        return new DriverFolderListing(root, state.Files, state.Skipped);
    }

    /// <summary>Whether a name is acceptable on the medium: Windows rules, well-formed text, no device names.</summary>
    public static bool IsValidName(string name)
    {
        if (name.Length is 0 or > MaxNameLength || name[^1] is '.' or ' ')
        {
            return false;
        }

        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c < ' ' || InvalidNameCharacters.Contains(c, StringComparison.Ordinal))
            {
                return false;
            }

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= name.Length || !char.IsLowSurrogate(name[i + 1]))
                {
                    return false;
                }

                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return false;
            }
        }

        var dot = name.IndexOf('.', StringComparison.Ordinal);
        return !ReservedNames.Contains((dot < 0 ? name : name[..dot]).TrimEnd(' '));
    }

    private static void Walk(DirectoryInfo directory, string relative, int depth, ScanState state, CancellationToken cancellationToken)
    {
        List<FileSystemInfo> entries;
        try
        {
            entries = [.. directory.EnumerateFileSystemInfos("*", Everything).OrderBy(entry => entry.Name, StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Reject(state.Root, "Driver.Reject.NotReadable", Display(relative));
        }

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entryRelative = relative.Length == 0 ? entry.Name : relative + Path.DirectorySeparatorChar + entry.Name;

            if (!IsValidName(entry.Name))
            {
                throw Reject(state.Root, "Driver.Reject.BadName", entryRelative);
            }

            if (IsLink(entry))
            {
                throw Reject(state.Root, "Driver.Reject.Link", entryRelative);
            }

            if (entry is DirectoryInfo child)
            {
                if (depth + 1 > state.Limits.MaxDepth)
                {
                    throw Reject(state.Root, "Driver.Reject.TooDeep", state.Limits.MaxDepth.ToString(CultureInfo.CurrentCulture), entryRelative);
                }

                Walk(child, entryRelative, depth + 1, state, cancellationToken);
            }
            else if (entry is FileInfo file)
            {
                AddFile(file, entryRelative, state);
            }
        }
    }

    private static void AddFile(FileInfo file, string relative, ScanState state)
    {
        var extension = file.Extension;
        if (!AllowedExtensions.Contains(extension))
        {
            var key = extension.Length == 0 ? "(none)" : extension.ToLowerInvariant();
            state.Skipped[key] = state.Skipped.GetValueOrDefault(key) + 1;
            return;
        }

        if (relative.Length > state.Limits.MaxRelativePathLength)
        {
            throw Reject(state.Root, "Driver.Reject.PathTooLong", relative);
        }

        long length;
        try
        {
            length = file.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw Reject(state.Root, "Driver.Reject.NotReadable", relative);
        }

        if (state.Files.Count + 1 > state.Limits.MaxFiles)
        {
            throw Reject(state.Root, "Driver.Reject.TooManyFiles", state.Limits.MaxFiles.ToString(CultureInfo.CurrentCulture));
        }

        state.Bytes += length;
        if (state.Bytes > state.Limits.MaxBytes)
        {
            throw Reject(state.Root, "Driver.Reject.TooLarge", FormatSize(state.Limits.MaxBytes));
        }

        state.Files.Add(new DriverFile(relative, length));
    }

    /// <summary>
    /// A directory that is a reparse point of any kind (symbolic link, junction, mount point) leads somewhere else.
    /// For files only real links count: OneDrive placeholders and compressed or deduplicated files are
    /// reparse points as well, and reading them is ordinary.
    /// </summary>
    internal static bool IsLink(FileSystemInfo entry)
    {
        if (entry.LinkTarget is not null)
        {
            return true;
        }

        return entry is DirectoryInfo && entry.Attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    private static string Display(string relative) => relative.Length == 0 ? "." : relative;

    private static string FormatSize(long bytes) =>
        bytes >= 1L << 30
            ? (bytes / (double)(1L << 30)).ToString("0.#", CultureInfo.CurrentCulture) + " GB"
            : (bytes / (double)(1L << 20)).ToString("0", CultureInfo.CurrentCulture) + " MB";

    private static BootrixException Reject(string folder, string reasonKey, params object?[] arguments) =>
        new(ErrorCode.DriverFolderRejected, $"{folder}: {reasonKey}")
        {
            Arguments = [folder, Localizer.Default.Get(reasonKey, arguments)],
        };

    private sealed class ScanState(string root, DriverLimits limits)
    {
        public string Root { get; } = root;

        public DriverLimits Limits { get; } = limits;

        public List<DriverFile> Files { get; } = [];

        public Dictionary<string, int> Skipped { get; } = new(StringComparer.OrdinalIgnoreCase);

        public long Bytes { get; set; }
    }
}
