// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;

namespace Bootrix.Core.Boot.Dos;

/// <summary>One file of a DOS system disk. Paths start at the root with a backslash and use 8.3 names in upper case.</summary>
public sealed record DosFile(string Path, byte[] Content, FileAttributes Attributes = FileAttributes.Archive)
{
    /// <summary>The attributes DOS expects on its system files: invisible in a directory listing and not offered for deletion.</summary>
    public const FileAttributes SystemFile = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReadOnly | FileAttributes.Archive;

    /// <summary>The folder the file lives in; empty for the root.</summary>
    public string Folder => Path[..Path.LastIndexOf('\\')];

    public static DosFile Text(string path, string text) =>
        new(path, Encoding.ASCII.GetBytes(text.ReplaceLineEndings("\r\n")));
}
