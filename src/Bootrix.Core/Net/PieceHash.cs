// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Net;

/// <summary>The expected digest of one contiguous slice of the file.</summary>
public sealed record PieceHash(long Offset, long Length, FileHash Hash)
{
    public long End => Offset + Length;

    /// <summary>
    /// Builds the list for equally sized pieces as used by Metalink; the last piece is whatever remains.
    /// </summary>
    public static IReadOnlyList<PieceHash> CreateUniform(long fileSize, long pieceLength, HashKind kind, IReadOnlyList<string> hexDigests)
    {
        ArgumentNullException.ThrowIfNull(hexDigests);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pieceLength);
        ArgumentOutOfRangeException.ThrowIfNegative(fileSize);

        var expected = (fileSize + pieceLength - 1) / pieceLength;
        if (hexDigests.Count != expected)
        {
            throw new ArgumentException($"{hexDigests.Count} piece digests given, {expected} needed for {fileSize} bytes.", nameof(hexDigests));
        }

        var pieces = new List<PieceHash>(hexDigests.Count);
        for (var i = 0; i < hexDigests.Count; i++)
        {
            var offset = i * pieceLength;
            pieces.Add(new PieceHash(offset, Math.Min(pieceLength, fileSize - offset), new FileHash(kind, hexDigests[i])));
        }

        return pieces;
    }
}
