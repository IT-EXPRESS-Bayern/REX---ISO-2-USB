// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Images.Compression;

public sealed record CompressedImageOptions
{
    /// <summary>
    /// Entry to read from a zip archive, matched against the full path and then the file name. When null
    /// the archive's image is chosen by <see cref="ZipEntrySelector"/>.
    /// </summary>
    public string? EntryName { get; init; }

    /// <summary>
    /// Examine headers and trailers before decoding and refuse truncated files. Turn off to peek into
    /// a damaged download.
    /// </summary>
    public bool CheckStructure { get; init; } = true;
}
