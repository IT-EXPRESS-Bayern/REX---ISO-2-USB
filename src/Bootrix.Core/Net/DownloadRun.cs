// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Runtime.ExceptionServices;
using Bootrix.Core.Errors;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Bootrix.Core.Net;

/// <summary>One attempt to get a file: probe, plan, transfer, verify, rename.</summary>
internal sealed class DownloadRun(
    DownloadRequest request,
    string destination,
    IProgress<DownloadProgress>? progress,
    Func<DownloadOptions, HttpMessageHandler> handlerFactory,
    TimeProvider time,
    ILogger log)
{
    private readonly DownloadOptions _options = request.Options;
    private readonly string _partPath = DownloadStateFile.PartPathFor(destination);
    private readonly string _statePath = DownloadStateFile.PathFor(destination);

    private sealed record Transfer(SafeFileHandle File, long Length, long ResumedBytes);

    public async Task<DownloadResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        using var http = new HttpClient(handlerFactory(_options), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };

        progress?.Report(new DownloadProgress(DownloadPhase.Connecting, 0, null, 0, null, 0));

        using var plan = await DownloadPlan.CreateAsync(http, request, time, log, cancellationToken).ConfigureAwait(false);
        var transfer = plan.Ranged
            ? await DownloadRangedAsync(http, plan, cancellationToken).ConfigureAwait(false)
            : await DownloadStreamAsync(http, plan, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<FileHash> hashes;
        try
        {
            hashes = await HashAsync(transfer, cancellationToken).ConfigureAwait(false);
            RandomAccess.FlushToDisk(transfer.File);
        }
        finally
        {
            transfer.File.Dispose();
        }

        CheckAgainstExpectations(transfer, hashes);

        File.Move(_partPath, destination, overwrite: true);
        DownloadStateFile.Delete(_statePath);

        var link = plan.Sources[0].Link;
        return new DownloadResult(destination, transfer.Length, hashes, link.Resolved, link.ETag, link.LastModified, transfer.ResumedBytes, time.GetElapsedTime(started));
    }

    private async Task<Transfer> DownloadRangedAsync(HttpClient http, DownloadPlan plan, CancellationToken cancellationToken)
    {
        var length = plan.Length!.Value;
        var done = await TryResumeAsync(plan, length, cancellationToken).ConfigureAwait(false);
        var resumed = done is not null;
        done ??= new RangeSet();

        if (!resumed)
        {
            DownloadStateFile.Delete(_statePath);
        }

        EnsureFreeSpace(length - done.Total);

        var file = File.OpenHandle(_partPath, resumed ? FileMode.Open : FileMode.Create, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous);
        try
        {
            if (!resumed)
            {
                // Sparse on most file systems. NTFS zero-fills up to the first out-of-order write, once.
                RandomAccess.SetLength(file, length);
            }

            var verifier = request.Pieces.Count > 0 ? new PieceVerifier(request.Pieces, file) : null;
            if (verifier is not null && resumed)
            {
                // Trust nothing from an earlier run that a digest can check.
                foreach (var index in await verifier.VerifyAllCoveredAsync(r => done.Covers(r.Start, r.End), cancellationToken).ConfigureAwait(false))
                {
                    var range = verifier.RangeOf(index);
                    done.Remove(range.Start, range.End);
                }
            }

            var resumedBytes = done.Total;
            var counter = new TransferCounter();
            counter.Seed(resumedBytes);

            var pool = new SourcePool(plan.Sources);
            var scheduler = new SegmentScheduler(length, done, WorkerCount(length - resumedBytes, pool), _options.MinSegmentSize, verifier is null ? null : verifier.AlignDown);
            var keeper = verifier is null ? null : new PieceKeeper(verifier, scheduler, counter, pool, log);
            var worker = new SegmentWorker(http, request, pool, scheduler, file, keeper, counter, new LinkRenewer(http, request, length, log), length, time, log);

            async Task SaveStateAsync(CancellationToken token)
            {
                var snapshot = scheduler.Snapshot();
                RandomAccess.FlushToDisk(file);
                var lead = plan.Sources[0].Link;
                await DownloadStateFile.WriteAsync(_statePath, new DownloadState
                {
                    Url = request.Url.AbsoluteUri,
                    ETag = lead.ETag,
                    LastModified = lead.LastModified,
                    Length = length,
                    Ranges = [.. snapshot.Ranges.Select(r => new StateRange(r.Start, r.End))],
                }, token).ConfigureAwait(false);
            }

            await using var reporter = new TransferReporter(progress, counter, length, _options, time, SaveStateAsync, log);
            reporter.Start();
            try
            {
                await RunRoundsAsync(worker, scheduler, pool, keeper, counter, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await reporter.StopAsync().ConfigureAwait(false);
                try
                {
                    await SaveStateAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (IOException ex)
                {
                    log.LogWarning(ex, "Could not write the resume file");
                }
            }

            return new Transfer(file, length, resumedBytes);
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    private async Task RunRoundsAsync(
        SegmentWorker worker,
        SegmentScheduler scheduler,
        SourcePool pool,
        PieceKeeper? keeper,
        TransferCounter counter,
        CancellationToken cancellationToken)
    {
        while (!scheduler.IsComplete)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var writtenBefore = counter.TotalWritten;
            var workers = WorkerCount(scheduler.Missing, pool);
            if (workers == 0)
            {
                throw new BootrixException(ErrorCode.DownloadFailed, "no usable source is left") { Arguments = ["no usable source"] };
            }

            await RunWorkersAsync(worker, workers, cancellationToken).ConfigureAwait(false);
            var rejected = keeper is null ? 0 : await keeper.SweepAsync(cancellationToken).ConfigureAwait(false);

            if (!scheduler.IsComplete && rejected == 0 && counter.TotalWritten == writtenBefore)
            {
                throw new BootrixException(ErrorCode.DownloadFailed, "no source delivered any data") { Arguments = ["no data received"] };
            }
        }
    }

    private static async Task RunWorkersAsync(SegmentWorker worker, int count, CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Exception? failure = null;

        async Task RunOneAsync()
        {
            try
            {
                await worker.RunAsync(stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The first real failure wins; the cancellations it causes in the others are not interesting.
                Interlocked.CompareExchange(ref failure, ex, null);
                await stop.CancelAsync().ConfigureAwait(false);
            }
        }

        await Task.WhenAll(Enumerable.Range(0, count).Select(_ => Task.Run(RunOneAsync, CancellationToken.None))).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private int WorkerCount(long missing, SourcePool pool)
    {
        var byWork = Math.Max(1, (missing + _options.MinSegmentSize - 1) / _options.MinSegmentSize);
        return (int)Math.Min(Math.Min(byWork, _options.MaxSegments), pool.Capacity(_options.MaxSegments));
    }

    private async Task<Transfer> DownloadStreamAsync(HttpClient http, DownloadPlan plan, CancellationToken cancellationToken)
    {
        DownloadStateFile.Delete(_statePath);
        if (plan.Length is { } announced)
        {
            EnsureFreeSpace(announced);
        }

        var file = File.OpenHandle(_partPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, FileOptions.Asynchronous);
        try
        {
            var counter = new TransferCounter();
            var transfer = new StreamTransfer(http, request, file, counter, new LinkRenewer(http, request, plan.Length, log), time, log);
            await using var reporter = new TransferReporter(progress, counter, plan.Length, _options, time, null, log);

            reporter.Start();
            long written;
            try
            {
                written = await transfer.RunAsync(plan.Sources[0], plan.TakeStreamResponse(), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await reporter.StopAsync().ConfigureAwait(false);
            }

            return new Transfer(file, written, 0);
        }
        catch
        {
            // A half-written stream cannot be resumed, so there is nothing worth keeping.
            file.Dispose();
            DownloadStateFile.TryDelete(_partPath);
            throw;
        }
    }

    /// <summary>
    /// Picks up an earlier run only if it demonstrably downloaded the same file: same length, same validator
    /// (ETag, else Last-Modified, else at least the same address) and a part file of the right size.
    /// </summary>
    private async Task<RangeSet?> TryResumeAsync(DownloadPlan plan, long length, CancellationToken cancellationToken)
    {
        var state = await DownloadStateFile.TryReadAsync(_statePath, cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            return null;
        }

        if (state.Length != length || !File.Exists(_partPath) || new FileInfo(_partPath).Length != length)
        {
            log.LogInformation("Discarding resume state: size does not match");
            return null;
        }

        if (!plan.Sources.Any(s => SameFile(state, s.Link)))
        {
            log.LogInformation("Discarding resume state: the file on the server has changed");
            return null;
        }

        var done = RangeSet.From(state.Ranges.Select(r => new ByteRange(r.Start, r.End)));
        log.LogInformation("Resuming with {Done} of {Length} bytes already on disk", done.Total, length);
        return done;
    }

    private bool SameFile(DownloadState state, SourceLink link)
    {
        if (state.ETag is not null || link.ETag is not null)
        {
            return string.Equals(state.ETag, link.ETag, StringComparison.Ordinal);
        }

        if (state.LastModified is not null || link.LastModified is not null)
        {
            return state.LastModified == link.LastModified;
        }

        return string.Equals(state.Url, request.Url.AbsoluteUri, StringComparison.Ordinal);
    }

    private async Task<IReadOnlyList<FileHash>> HashAsync(Transfer transfer, CancellationToken cancellationToken)
    {
        var kinds = request.ExpectedHashes.Select(h => h.Kind).Append(HashKind.Sha256);
        var lastReport = time.GetTimestamp();

        void OnProgress(long done)
        {
            if (progress is null || (done < transfer.Length && time.GetElapsedTime(lastReport) < _options.ProgressInterval))
            {
                return;
            }

            lastReport = time.GetTimestamp();
            progress.Report(new DownloadProgress(DownloadPhase.Verifying, done, transfer.Length, 0, null, 0));
        }

        return await FileHasher.ComputeAsync(transfer.File, transfer.Length, kinds, OnProgress, cancellationToken).ConfigureAwait(false);
    }

    private void CheckAgainstExpectations(Transfer transfer, IReadOnlyList<FileHash> hashes)
    {
        if (request.ExpectedSize is { } size && size != transfer.Length)
        {
            Discard();
            throw new BootrixException(ErrorCode.DownloadHashMismatch, $"received {transfer.Length} bytes, {size} expected");
        }

        foreach (var expected in request.ExpectedHashes)
        {
            var actual = hashes.First(h => h.Kind == expected.Kind);
            if (actual != expected)
            {
                Discard();
                throw new BootrixException(ErrorCode.DownloadHashMismatch, $"{expected.Kind} is {actual.Hex}, expected {expected.Hex}");
            }
        }
    }

    private void Discard()
    {
        DownloadStateFile.TryDelete(_partPath);
        DownloadStateFile.Delete(_statePath);
    }

    private void EnsureFreeSpace(long needed)
    {
        if (needed <= 0)
        {
            return;
        }

        long available;
        string root;
        try
        {
            var drive = new DriveInfo(Path.GetDirectoryName(destination)!);
            root = drive.Name;
            available = drive.AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            log.LogDebug(ex, "Free space of the target could not be determined");
            return;
        }

        if (available < needed)
        {
            throw new BootrixException(ErrorCode.InsufficientSpace, $"{available} bytes free on {root}, {needed} needed")
            {
                Arguments = [root, FormatSize(needed)],
            };
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 40 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 40):0.0} TiB"),
        >= 1L << 30 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 30):0.0} GiB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (double)(1L << 20):0.0} MiB"),
    };
}
