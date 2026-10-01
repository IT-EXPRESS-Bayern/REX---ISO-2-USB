// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Writing.Windows;

/// <summary>An image whose files can be read from a directory: an ISO that Windows has attached, or an unpacked tree.</summary>
public sealed class DirectoryMediaSource : IWindowsMediaSource
{
    // Hidden and system files are part of the medium (bootmgr carries both attributes), so nothing is skipped.
    private static readonly EnumerationOptions Everything = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
    };

    private readonly string _root;

    private DirectoryMediaSource(string root, List<MediaSourceFile> files, List<string> directories)
    {
        _root = root;
        Files = files;
        Directories = directories;
    }

    public IReadOnlyList<MediaSourceFile> Files { get; }

    public IReadOnlyList<string> Directories { get; }

    public static DirectoryMediaSource Scan(string root, CancellationToken cancellationToken = default)
    {
        var info = new DirectoryInfo(root);
        var files = new List<MediaSourceFile>();
        var directories = new List<string>();
        var pending = new Stack<(DirectoryInfo Directory, string Prefix)>();
        pending.Push((info, string.Empty));

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, prefix) = pending.Pop();
            foreach (var entry in directory.EnumerateFileSystemInfos("*", Everything))
            {
                var path = prefix + entry.Name;
                if (MediaExclusions.IsExcluded(path))
                {
                    continue;
                }

                if (entry is DirectoryInfo child)
                {
                    // A junction would lead out of the image or around in circles.
                    if (child.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }

                    directories.Add(path);
                    pending.Push((child, path + "/"));
                }
                else if (entry is FileInfo file)
                {
                    files.Add(new MediaSourceFile(path, file.Length, file.LastWriteTimeUtc));
                }
            }
        }

        return new DirectoryMediaSource(info.FullName, files, directories);
    }

    public Stream OpenRead(string path) =>
        new FileStream(LocalPath(path)!, FileMode.Open, FileAccess.Read, FileShare.Read, 0, FileOptions.SequentialScan | FileOptions.Asynchronous);

    public string? LocalPath(string path) =>
        Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar));

    public void Dispose()
    {
    }
}
