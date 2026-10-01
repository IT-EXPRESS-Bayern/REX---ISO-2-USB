// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Writing.Linux;

/// <summary>
/// A target volume that is a directory: a mounted stick addressed as <c>\\?\Volume{guid}\</c> on Windows, or any
/// folder. All paths are checked so that nothing the image contains can reach outside of the root.
/// </summary>
public sealed class DirectoryVolume : ITargetVolume
{
    private const int CopyBuffer = 1024 * 1024;

    private readonly string _root;

    public DirectoryVolume(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        _root = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
    }

    public void CreateDirectory(string path) => Directory.CreateDirectory(Resolve(path));

    public Stream CreateFile(string path, long expectedLength)
    {
        var full = Resolve(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        // A file left over from an earlier run may be read-only, hidden and system (ldlinux.sys).
        if (File.Exists(full))
        {
            File.SetAttributes(full, FileAttributes.Normal);
        }

        var stream = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None, CopyBuffer, FileOptions.SequentialScan);
        try
        {
            if (expectedLength > 0)
            {
                // Telling the file system the final size up front keeps the file in one piece on a fresh volume.
                stream.SetLength(expectedLength);
                stream.Position = 0;
            }

            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public bool FileExists(string path) => File.Exists(Resolve(path));

    public IReadOnlyList<string> List(string directory)
    {
        var full = directory.Length == 0 ? _root : Resolve(directory);
        return Directory.Exists(full)
            ? [.. Directory.EnumerateFileSystemEntries(full).Select(Path.GetFileName).OfType<string>().Order(StringComparer.OrdinalIgnoreCase)]
            : [];
    }

    public Stream OpenRead(string path) =>
        new FileStream(Resolve(path), FileMode.Open, FileAccess.Read, FileShare.Read, CopyBuffer, FileOptions.SequentialScan);

    public void SetAttributes(string path, FileAttributes attributes) => File.SetAttributes(Resolve(path), attributes);

    public void Delete(string path)
    {
        var full = Resolve(path);
        if (File.Exists(full))
        {
            File.SetAttributes(full, FileAttributes.Normal);
            File.Delete(full);
        }
    }

    private string Resolve(string relative)
    {
        var segments = relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            // Names Windows cannot create or later remove: a trailing dot or space is silently dropped by Win32.
            if (segment is "." or ".." || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || segment[^1] is '.' or ' ')
            {
                throw new ArgumentException($"The path '{relative}' is not a valid path on the medium.", nameof(relative));
            }
        }

        return segments.Length == 0 ? _root : _root + string.Join(Path.DirectorySeparatorChar, segments);
    }
}
