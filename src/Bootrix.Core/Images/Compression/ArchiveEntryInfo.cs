// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Images.Compression;

/// <summary>A file inside a zip archive that could serve as the image.</summary>
public sealed record ArchiveEntryInfo(string Name, long Length, long CompressedLength);
