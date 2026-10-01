// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Images.Compression;

internal enum StructureVerdict
{
    /// <summary>The container's end markers and index are present and consistent.</summary>
    Intact,

    /// <summary>The container is cut off or its trailer is inconsistent.</summary>
    Broken,

    /// <summary>The format carries nothing that can be checked without decompressing everything.</summary>
    Unverifiable,
}

/// <summary>
/// Result of examining a compressed file's headers and trailers without decoding the payload.
/// </summary>
/// <param name="Verdict">Whether the container looks complete.</param>
/// <param name="UncompressedSize">Exact decoded size taken from the container's own metadata, if it has any.</param>
/// <param name="SizeHint">A size that is only known modulo 2^32 (gzip ISIZE).</param>
/// <param name="Problem">Technical description for the log when the verdict is <see cref="StructureVerdict.Broken"/>.</param>
internal readonly record struct StructureReport(
    StructureVerdict Verdict,
    long? UncompressedSize = null,
    long? SizeHint = null,
    string? Problem = null)
{
    public static StructureReport Broken(string problem) => new(StructureVerdict.Broken, Problem: problem);

    public static StructureReport Unverifiable(long? sizeHint = null) =>
        new(StructureVerdict.Unverifiable, SizeHint: sizeHint);
}
