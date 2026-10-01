// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Writing.Windows;

/// <summary>A file as it was handed to the target, with the SHA-256 of its contents for the read-back.</summary>
/// <param name="Path">Relative path on the target, with '/'.</param>
public sealed record CopiedFile(string Path, long Length, byte[] Sha256);

/// <param name="BytesDone">Bytes of the plan that are on the target, counting a split image as the size of the image it was cut from.</param>
/// <param name="File">The file that is being written right now.</param>
public readonly record struct CopyProgress(long BytesDone, string? File);
