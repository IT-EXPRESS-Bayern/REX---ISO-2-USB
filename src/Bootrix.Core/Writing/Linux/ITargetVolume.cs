// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Writing.Linux;

/// <summary>
/// The file system of the medium as the media builder sees it: a tree of directories and files addressed by
/// relative paths with '/' separators. On Windows it is the mounted volume; tests put the same code in front of a folder.
/// </summary>
public interface ITargetVolume
{
    void CreateDirectory(string path);

    /// <summary>Creates or replaces a file, creating missing parent directories. <paramref name="expectedLength"/> lets the volume reserve space in one piece.</summary>
    Stream CreateFile(string path, long expectedLength);

    bool FileExists(string path);

    /// <summary>The names of the files and folders directly below a directory; the root is the empty path.</summary>
    IReadOnlyList<string> List(string directory);

    Stream OpenRead(string path);

    void SetAttributes(string path, FileAttributes attributes);

    void Delete(string path);
}
