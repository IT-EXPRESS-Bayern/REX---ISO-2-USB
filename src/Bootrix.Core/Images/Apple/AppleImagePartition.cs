// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Images.Apple;

/// <summary>A partition of the image as the partition table lists it. Offset and length are in bytes.</summary>
public sealed record AppleImagePartition(
    int Index,
    string Name,
    string Type,
    long Offset,
    long Length,
    AppleFileSystem FileSystem,
    bool IsBlessed);
