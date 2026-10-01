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
    /// Refuse files whose headers and trailers show them to be cut off. Turn off to peek into a damaged
    /// download; the findings are then available as <c>Structure</c>.
    /// </summary>
    public bool CheckStructure { get; init; } = true;
}
