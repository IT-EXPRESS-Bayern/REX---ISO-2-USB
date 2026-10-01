// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Writing.Windows;

/// <summary>A file of the image to be copied. <see cref="Path"/> is relative to the image root and always uses '/'.</summary>
public sealed record MediaSourceFile(string Path, long Length, DateTime? LastWriteUtc = null)
{
    public string Name => Path[(Path.LastIndexOf('/') + 1)..];

    public string Directory => Path.Contains('/') ? Path[..Path.LastIndexOf('/')] : string.Empty;
}
