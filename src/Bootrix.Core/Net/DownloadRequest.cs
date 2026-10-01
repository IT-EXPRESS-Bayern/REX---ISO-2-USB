// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Net;

public sealed record DownloadRequest
{
    public DownloadRequest(Uri url)
        : this([new MirrorSource(url)])
    {
    }

    /// <summary>Several sources of the same file; segments are spread over them. The first one is the primary source.</summary>
    public DownloadRequest(IReadOnlyList<MirrorSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        if (sources.Count == 0)
        {
            throw new ArgumentException("At least one source is required.", nameof(sources));
        }

        foreach (var source in sources)
        {
            if (!IsWeb(source.Url))
            {
                throw new ArgumentException($"Only absolute http(s) addresses can be downloaded: {source.Url}", nameof(sources));
            }
        }

        Sources = sources;
    }

    public IReadOnlyList<MirrorSource> Sources { get; }

    public Uri Url => Sources[0].Url;

    /// <summary>Digests the finished file must have. All of them are computed in one pass over the file.</summary>
    public IReadOnlyList<FileHash> ExpectedHashes { get; init; } = [];

    public long? ExpectedSize { get; init; }

    /// <summary>
    /// Digests of consecutive slices covering the whole file. Slices are checked as soon as they are complete,
    /// a bad slice is fetched again, and a source that delivered one is not used further when others remain.
    /// </summary>
    public IReadOnlyList<PieceHash> Pieces { get; init; } = [];

    /// <summary>
    /// Returns a fresh address for the primary source. Called when the server answers 403 or 410,
    /// which is how time-limited links (Microsoft's expire after 24 hours) announce that they are gone.
    /// </summary>
    public Func<CancellationToken, Task<Uri>>? LinkResolver { get; init; }

    public DownloadOptions Options { get; init; } = new();

    internal void Validate()
    {
        Options.Validate();

        foreach (var hash in ExpectedHashes)
        {
            if (!hash.Kind.IsAcceptedForVerification())
            {
                throw new ArgumentException($"{hash.Kind} is not accepted as an integrity check.", nameof(ExpectedHashes));
            }
        }

        if (ExpectedHashes.GroupBy(h => h.Kind).Any(g => g.Select(h => h.Hex).Distinct().Count() > 1))
        {
            throw new ArgumentException("Contradicting digests for the same algorithm.", nameof(ExpectedHashes));
        }

        if (Pieces.Count == 0)
        {
            return;
        }

        long next = 0;
        foreach (var piece in Pieces)
        {
            if (piece.Offset != next || piece.Length <= 0 || !piece.Hash.Kind.IsAcceptedForVerification())
            {
                throw new ArgumentException("Pieces must be consecutive, non-empty and use an accepted algorithm.", nameof(Pieces));
            }

            next = piece.End;
        }

        if (ExpectedSize is { } size && size != next)
        {
            throw new ArgumentException($"Pieces cover {next} bytes but the file is {size} bytes.", nameof(Pieces));
        }
    }

    private static bool IsWeb(Uri url) =>
        url.IsAbsoluteUri && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps);
}
