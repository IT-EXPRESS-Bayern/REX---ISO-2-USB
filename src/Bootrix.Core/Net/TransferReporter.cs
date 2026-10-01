// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Jobs;
using Microsoft.Extensions.Logging;

namespace Bootrix.Core.Net;

/// <summary>
/// Ticks while a transfer runs: publishes progress with speed and ETA and periodically persists the resume
/// state. Doing both on one timer keeps the workers free of anything but moving bytes.
/// </summary>
internal sealed class TransferReporter(
    IProgress<DownloadProgress>? progress,
    TransferCounter counter,
    long? total,
    DownloadOptions options,
    TimeProvider time,
    Func<CancellationToken, Task>? saveState,
    ILogger log) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly SpeedEstimator _speed = new(time);
    private Task _loop = Task.CompletedTask;

    public void Start()
    {
        Publish();
        _loop = Task.Run(() => RunAsync(_stop.Token));
    }

    public async Task StopAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _loop.ConfigureAwait(false);
        Publish();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var lastSave = time.GetTimestamp();

        try
        {
            using var timer = new PeriodicTimer(options.ProgressInterval, time);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                Publish();

                if (saveState is not null && time.GetElapsedTime(lastSave) >= options.StateSaveInterval)
                {
                    lastSave = time.GetTimestamp();
                    await SaveAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopped by StopAsync.
        }
    }

    private void Publish()
    {
        if (progress is null)
        {
            return;
        }

        var done = counter.Bytes;
        _speed.Update(done);
        progress.Report(new DownloadProgress(
            DownloadPhase.Downloading,
            done,
            total,
            _speed.BytesPerSecond,
            total is { } t ? _speed.Remaining(done, t) : null,
            counter.Active));
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await saveState!(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A missed save only means a little more is repeated after an interruption.
            log.LogWarning(ex, "Could not write the resume file");
        }
    }
}
