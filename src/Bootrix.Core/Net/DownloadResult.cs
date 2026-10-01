// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Net;

/// <param name="Path">The finished file. It only exists under this name after every expected digest matched.</param>
/// <param name="Hashes">SHA-256 plus every algorithm that was expected, from a single pass over the file.</param>
/// <param name="FinalUrl">Address after redirects that served the data.</param>
/// <param name="ResumedBytes">Bytes taken over from an earlier, interrupted run.</param>
public sealed record DownloadResult(
    string Path,
    long Length,
    IReadOnlyList<FileHash> Hashes,
    Uri FinalUrl,
    string? ETag,
    DateTimeOffset? LastModified,
    long ResumedBytes,
    TimeSpan Elapsed)
{
    public string Sha256 => Hashes.First(h => h.Kind == HashKind.Sha256).Hex;
}
