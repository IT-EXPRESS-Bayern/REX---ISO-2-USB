// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Bootrix.Core.Errors;
using Bootrix.Core.Jobs;
using Bootrix.Core.Optical.Reading;
using Microsoft.Extensions.Logging;

namespace Bootrix.Core.Optical.Jobs;

/// <summary>Checks and helpers the burn jobs share.</summary>
internal static class BurnSupport
{
    /// <summary>
    /// Makes sure the disc in the drive can take a write that starts at sector 0. Raw images and freshly built file systems
    /// both assume that; appending to a closed or multi-session disc would put every internal address off by the session start.
    /// </summary>
    public static async Task<OpticalMedia> RequireWritableAsync(
        IOpticalService service,
        OpticalDrive drive,
        long sectorsNeeded,
        BurnOptions options,
        CancellationToken cancellationToken)
    {
        if (!drive.CanRecord)
        {
            throw new BootrixException(ErrorCode.NoRecorder, drive.DisplayName);
        }

        var media = await service.QueryMediaAsync(drive, cancellationToken).ConfigureAwait(false);
        if (!media.IsPresent)
        {
            throw new BootrixException(ErrorCode.MediaNotSupported, $"no disc in {drive.DisplayName}");
        }

        if (media.Condition == OpticalMediaCondition.NotWritable || !drive.Capabilities.CanWrite(media.Type))
        {
            throw new BootrixException(ErrorCode.MediaNotSupported, $"{media.Type} ({media.State}) in {drive.DisplayName}");
        }

        var overwrite = media.Condition == OpticalMediaCondition.Rewritable && options.ForceOverwrite;
        if (media.Condition is not OpticalMediaCondition.Blank && !overwrite)
        {
            throw new BootrixException(ErrorCode.MediaNotBlank, $"{media.Type} ({media.State}) in {drive.DisplayName}");
        }

        // A rewritable disc is written from sector 0, so the whole disc counts, not what its (stale) session information calls free.
        var available = overwrite ? Math.Max(media.TotalSectors, media.FreeSectors) : media.FreeSectors;
        if (sectorsNeeded > available)
        {
            throw new BootrixException(ErrorCode.DeviceTooSmall, drive.DisplayName)
            {
                Arguments = [SectorMath.FormatBytes(SectorMath.ToBytes(sectorsNeeded)), SectorMath.FormatBytes(SectorMath.ToBytes(available))],
            };
        }

        return media;
    }

    /// <summary>
    /// Runs one write per drive at the same time and reports the combined progress. A drive that fails does not stop
    /// the others; the caller decides what a partial result means.
    /// </summary>
    public static async Task<IReadOnlyList<DriveBurnResult>> BurnInParallelAsync(
        IReadOnlyList<OpticalDrive> drives,
        long sectorsPerDrive,
        JobContext context,
        Func<OpticalDrive, IProgress<BurnProgress>, Task> burn,
        CancellationToken cancellationToken)
    {
        var combined = new MultiDriveProgress(drives.Count);
        var reporting = new object();
        var totalBytes = SectorMath.ToBytes(sectorsPerDrive) * drives.Count;

        async Task<DriveBurnResult> RunOne(OpticalDrive drive, int index)
        {
            var started = Stopwatch.GetTimestamp();
            var progress = new SynchronousProgress<BurnProgress>(p =>
            {
                // Every drive reports from its own thread, the job context is not built for that.
                lock (reporting)
                {
                    combined.Update(index, p);
                    context.ReportStep(combined.Fraction, SectorMath.ToBytes(combined.SectorsWritten), totalBytes, combined.Phase?.ToString());
                }
            });

            try
            {
                await burn(drive, progress).ConfigureAwait(false);
                return new DriveBurnResult(drive, true, null, Stopwatch.GetElapsedTime(started));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                context.Log.LogError(ex, "Burning to {Drive} failed", drive.DisplayName);
                return new DriveBurnResult(drive, false, ex, Stopwatch.GetElapsedTime(started));
            }
        }

        var results = await Task.WhenAll(drives.Select(RunOne)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return results;
    }

    /// <summary>The first failure as the exception the job ends with.</summary>
    public static Exception? FirstFailure(IEnumerable<DriveBurnResult> results)
    {
        var failed = results.FirstOrDefault(r => !r.Succeeded);
        return failed is null
            ? null
            : failed.Error is BootrixException known
                ? known
                : new BootrixException(ErrorCode.BurnFailed, failed.Drive.DisplayName, failed.Error) { Arguments = [failed.Error?.Message] };
    }

    /// <summary>
    /// Opens the drive for reading once Windows has taken the new disc in. Right after a burn the volume is gone for a
    /// few seconds, and the capacity can read zero until the file system has been mounted.
    /// </summary>
    public static async Task<ISectorReader> OpenReaderWhenReadyAsync(
        IOpticalService service,
        OpticalDrive drive,
        long sectorsNeeded,
        int attempts,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        BootrixException? last = null;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (attempt > 0 && delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                var reader = service.OpenSectorReader(drive);
                var reported = reader.SectorCount;
                if (reported >= sectorsNeeded)
                {
                    return reader;
                }

                reader.Dispose();
                last = new BootrixException(ErrorCode.DeviceNotFound, $"{drive.DisplayName} reports {reported} sectors, {sectorsNeeded} expected");
            }
            catch (BootrixException ex) when (ex.Code is ErrorCode.DeviceNotFound or ErrorCode.DeviceBusy or ErrorCode.VolumeNotMounted)
            {
                last = ex;
            }
        }

        throw last ?? new BootrixException(ErrorCode.DeviceNotFound, drive.DisplayName);
    }

    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
