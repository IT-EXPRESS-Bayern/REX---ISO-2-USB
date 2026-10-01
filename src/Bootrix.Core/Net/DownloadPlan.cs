// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bootrix.Core.Errors;
using Microsoft.Extensions.Logging;

namespace Bootrix.Core.Net;

/// <summary>
/// The outcome of probing: how the file will be fetched (segmented over ranges, or as a single stream) and
/// from which sources. Holds the probe's open response in the single-stream case until the transfer takes it over.
/// </summary>
internal sealed class DownloadPlan : IDisposable
{
    private HttpResponseMessage? _streamResponse;

    private DownloadPlan(long? length, bool ranged, IReadOnlyList<DownloadSource> sources, HttpResponseMessage? streamResponse)
    {
        Length = length;
        Ranged = ranged;
        Sources = sources;
        _streamResponse = streamResponse;
    }

    public long? Length { get; }

    public bool Ranged { get; }

    public IReadOnlyList<DownloadSource> Sources { get; }

    public static async Task<DownloadPlan> CreateAsync(HttpClient http, DownloadRequest request, TimeProvider time, ILogger log, CancellationToken cancellationToken)
    {
        var probed = await new SourceProber(http, request, time, log).ProbeAllAsync(cancellationToken).ConfigureAwait(false);

        // Segmented download needs a source that proved range support and told its length; mirrors must agree on that length.
        var rangeCapable = probed.Where(p => p.Probe!.RangeSupported && p.Probe.Length is not null).ToList();
        var ranged = rangeCapable.Count > 0;
        var length = ranged ? rangeCapable[0].Probe!.Length : probed[0].Probe!.Length;
        var chosen = ranged ? rangeCapable.Where(p => p.Probe!.Length == length).ToList() : [probed[0]];

        var expectedLength = request.ExpectedSize ?? (request.Pieces.Count > 0 ? request.Pieces[^1].End : null);
        if (expectedLength is { } expected && length is { } announced && expected != announced)
        {
            foreach (var source in probed)
            {
                source.Probe!.Dispose();
            }

            throw new BootrixException(ErrorCode.DownloadFailed, $"the server reports {announced} bytes, {expected} expected") { Arguments = ["unexpected size"] };
        }

        foreach (var left in probed.Except(chosen))
        {
            log.LogInformation("Not using {Url}: {Reason}", request.Sources[left.Index].Url, ranged ? "no range support or different length" : "no ranges, using one connection");
        }

        var sources = chosen
            .Select(p => new DownloadSource(p.Index, request.Sources[p.Index], new SourceLink(p.Origin, p.Probe!.Resolved, p.Probe.ETag, p.Probe.LastModified, 0)))
            .ToList();

        // Without range support the probe's own response already is the download, so keep it open for the stream path.
        var streamResponse = ranged ? null : probed[0].Probe!.Body;
        foreach (var other in probed.Where(p => p.Probe!.Body is not null && !ReferenceEquals(p.Probe.Body, streamResponse)))
        {
            other.Probe!.Dispose();
        }

        log.LogInformation(
            "{Mode} download of {Length} from {Count} source(s)",
            ranged ? "Segmented" : "Single-stream",
            length?.ToString(CultureInfo.InvariantCulture) ?? "unknown length",
            sources.Count);

        return new DownloadPlan(length, ranged, sources, streamResponse);
    }

    /// <summary>Hands the open response over to the stream transfer, which disposes it.</summary>
    public HttpResponseMessage? TakeStreamResponse() => Interlocked.Exchange(ref _streamResponse, null);

    public void Dispose() => _streamResponse?.Dispose();
}
