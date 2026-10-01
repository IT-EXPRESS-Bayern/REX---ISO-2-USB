// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Writing.Windows;

/// <summary>
/// The file tree of a Windows setup image, read either from an attached ISO or straight out of the image
/// stream. The copy does not care which: it takes a listing, then opens file after file.
/// </summary>
public interface IWindowsMediaSource : IDisposable
{
    IReadOnlyList<MediaSourceFile> Files { get; }

    /// <summary>All directories including empty ones, paths relative to the root with '/'.</summary>
    IReadOnlyList<string> Directories { get; }

    /// <exception cref="FileNotFoundException">The file is not part of the image.</exception>
    Stream OpenRead(string path);

    /// <summary>The path of the file in the real file system, for libraries that insist on a file name; null when the file only exists inside an image stream.</summary>
    string? LocalPath(string path);
}
