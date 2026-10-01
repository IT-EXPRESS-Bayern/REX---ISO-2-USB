// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Enumeration;
using Bootrix.Core.Tiny;

namespace Bootrix.Windows.Dism;

/// <summary>File and registry access to a mounted Windows image.</summary>
public sealed class ImageFileSystem : IImageFileSystem
{
    private const int CopyBufferBytes = 1024 * 1024;

    public IOfflineHive LoadHive(string mountDirectory, RegistryHive hive)
    {
        var relative = hive switch
        {
            RegistryHive.Software => Path.Combine("Windows", "System32", "config", "SOFTWARE"),
            RegistryHive.System => Path.Combine("Windows", "System32", "config", "SYSTEM"),
            RegistryHive.Default => Path.Combine("Windows", "System32", "config", "DEFAULT"),
            RegistryHive.NtUser => Path.Combine("Users", "Default", "NTUSER.DAT"),
            _ => throw new ArgumentOutOfRangeException(nameof(hive)),
        };

        return OfflineHive.Load(Path.Combine(mountDirectory, relative));
    }

    public Task DeleteAsync(string path, bool takeOwnership, CancellationToken cancellationToken)
    {
        return Task.Run(
            () =>
            {
                foreach (var target in Expand(path))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FileOwnership.DeleteTree(target);
                }
            },
            cancellationToken);
    }

    public Task ReplaceWithEmptyFileAsync(string path, CancellationToken cancellationToken)
    {
        return Task.Run(
            () =>
            {
                FileOwnership.DeleteTree(path);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.Create(path).Dispose();
            },
            cancellationToken);
    }

    public Task RebuildWinSxsAsync(string winSxsPath, IReadOnlyList<string> keepPatterns, CancellationToken cancellationToken)
    {
        return Task.Run(
            () =>
            {
                var edited = winSxsPath + "_edit";
                FileOwnership.DeleteTree(edited);
                Directory.CreateDirectory(edited);

                foreach (var entry in Directory.EnumerateFileSystemEntries(winSxsPath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(entry);
                    if (keepPatterns.Any(p => FileSystemName.MatchesSimpleExpression(p, name, ignoreCase: true)))
                    {
                        CopyEntry(entry, Path.Combine(edited, name));
                    }
                }

                FileOwnership.DeleteTree(winSxsPath);
                Directory.Move(edited, winSxsPath);
            },
            cancellationToken);
    }

    public long GetFreeBytes(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path)) ?? path;
        return new DriveInfo(root).AvailableFreeSpace;
    }

    public async Task CopyDirectoryAsync(string source, string destination, Func<string, bool>? filter, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var files = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .Where(f => filter is null || filter(f))
            .Select(f => new FileInfo(f))
            .ToList();
        var total = Math.Max(1, files.Sum(f => f.Length));
        long done = 0;

        var buffer = new byte[CopyBufferBytes];
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(destination, Path.GetRelativePath(source, file.FullName));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            await using (var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferBytes, FileOptions.SequentialScan | FileOptions.Asynchronous))
            await using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferBytes, FileOptions.Asynchronous))
            {
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    done += read;
                    progress?.Report((double)done / total);
                }
            }

            // Files from an ISO are read-only; the copy has to be editable.
            File.SetAttributes(target, FileAttributes.Normal);
        }

        progress?.Report(1);
    }

    /// <summary>A path whose last segment contains * is expanded inside its parent folder.</summary>
    internal static IEnumerable<string> Expand(string path)
    {
        var name = Path.GetFileName(path);
        if (!name.Contains('*', StringComparison.Ordinal) && !name.Contains('?', StringComparison.Ordinal))
        {
            yield return path;
            yield break;
        }

        var parent = Path.GetDirectoryName(path);
        if (parent is null || !Directory.Exists(parent))
        {
            yield break;
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(parent, name))
        {
            yield return entry;
        }
    }

    private static void CopyEntry(string source, string destination)
    {
        if (Directory.Exists(source))
        {
            Directory.CreateDirectory(destination);
            foreach (var child in Directory.EnumerateFileSystemEntries(source))
            {
                CopyEntry(child, Path.Combine(destination, Path.GetFileName(child)));
            }

            return;
        }

        File.Copy(source, destination, overwrite: true);
        File.SetAttributes(destination, FileAttributes.Normal);
    }
}
