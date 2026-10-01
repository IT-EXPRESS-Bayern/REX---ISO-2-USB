// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Core.Net;

/// <summary>
/// Downloads a file over several parallel range requests, resumes interrupted transfers and only publishes
/// the result under its final name after every expected digest matched.
/// </summary>
/// <remarks>
/// While a transfer runs, <c>&lt;target&gt;.part</c> holds the data and <c>&lt;target&gt;.btxdl</c> the list of finished
/// ranges. Both are removed on success and on a digest mismatch, and kept after cancellation or a network failure.
/// </remarks>
public sealed class SegmentedDownloader
{
    // An in-flight change of the remote file restarts from scratch, but not forever.
    private const int MaxRestarts = 2;

    private readonly ILogger _log;
    private readonly Func<DownloadOptions, HttpMessageHandler> _handlerFactory;
    private readonly TimeProvider _time;

    /// <param name="handlerFactory">
    /// Creates the message handler for one download. The default is a <see cref="SocketsHttpHandler"/> that does not follow
    /// redirects itself; a replacement must not either, or the redirect chain is no longer resolved only once.
    /// </param>
    public SegmentedDownloader(
        ILogger<SegmentedDownloader>? logger = null,
        Func<DownloadOptions, HttpMessageHandler>? handlerFactory = null,
        TimeProvider? timeProvider = null)
    {
        _log = logger ?? NullLogger<SegmentedDownloader>.Instance;
        _handlerFactory = handlerFactory ?? DownloadHttp.CreateDefaultHandler;
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<DownloadResult> DownloadAsync(
        DownloadRequest request,
        string destinationPath,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        request.Validate();

        var destination = Path.GetFullPath(destinationPath);

        for (var restarts = 0; ; restarts++)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                return await new DownloadRun(request, destination, progress, _handlerFactory, _time, _log)
                    .ExecuteAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (ContentChangedException ex)
            {
                _log.LogWarning("The file changed on the server during the download: {Reason}", ex.Message);
                DownloadStateFile.TryDelete(DownloadStateFile.PartPathFor(destination));
                DownloadStateFile.Delete(DownloadStateFile.PathFor(destination));

                if (restarts >= MaxRestarts)
                {
                    throw new BootrixException(ErrorCode.DownloadFailed, ex.Message, ex) { Arguments = [ex.Message] };
                }
            }
            catch (BootrixException ex) when (ex.Code == ErrorCode.DownloadHashMismatch)
            {
                // Pieces that keep failing their digest leave nothing worth resuming.
                DownloadStateFile.TryDelete(DownloadStateFile.PartPathFor(destination));
                DownloadStateFile.Delete(DownloadStateFile.PathFor(destination));
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TransientDownloadException or LinkExpiredException or HttpRequestException)
            {
                throw new BootrixException(ErrorCode.DownloadFailed, ex.Message, ex) { Arguments = [ex.Message] };
            }
        }
    }
}
