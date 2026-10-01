// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;

namespace Bootrix.Core.Net;

/// <param name="Size">Byte length announced by the metalink, if any.</param>
/// <param name="Pieces">Slice digests; empty unless the metalink gave both a size and a complete list.</param>
/// <param name="Mirrors">http(s) sources ordered by priority.</param>
public sealed record MetalinkFile(
    string Name,
    long? Size,
    IReadOnlyList<FileHash> Hashes,
    IReadOnlyList<PieceHash> Pieces,
    IReadOnlyList<MirrorSource> Mirrors)
{
    /// <summary>
    /// Turns the entry into a download over all its mirrors. Mirrors in <paramref name="preferredLocation"/>
    /// (a country code such as "DE") are tried first, then the rest by priority.
    /// </summary>
    public DownloadRequest ToRequest(string? preferredLocation = null, DownloadOptions? options = null)
    {
        if (Mirrors.Count == 0)
        {
            throw new BootrixException(ErrorCode.MetalinkInvalid, $"{Name}: no http(s) mirror") { Arguments = [Name] };
        }

        var ordered = Mirrors
            .OrderBy(m => string.Equals(m.Location, preferredLocation, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(m => m.Priority)
            .ToList();

        return new DownloadRequest(ordered)
        {
            ExpectedHashes = [.. Hashes.Where(h => h.Kind.IsAcceptedForVerification())],
            ExpectedSize = Size,
            Pieces = Pieces,
            Options = options ?? new DownloadOptions(),
        };
    }
}
