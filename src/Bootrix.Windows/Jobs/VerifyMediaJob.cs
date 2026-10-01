// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Images.Compression;
using Bootrix.Core.Jobs;
using Bootrix.Core.Storage;
using Bootrix.Core.Writing.Raw;
using Bootrix.Core.Writing.Verify;
using Bootrix.Windows.Platform;
using Bootrix.Windows.Storage;
using Microsoft.Extensions.Logging;

namespace Bootrix.Windows.Jobs;

public sealed record VerifyTargetRequest(StorageDevice Device, DiskIdentity ConfirmedIdentity);

public sealed record VerifyMediaRequest
{
    public required string ImagePath { get; init; }

    public required IReadOnlyList<VerifyTargetRequest> Targets { get; init; }

    public VerifyMode Mode { get; init; } = VerifyMode.Auto;

    public string? ArchiveEntry { get; init; }

    public BlockMapUse BlockMap { get; init; } = BlockMapUse.Auto;
}

/// <summary>
/// "Check the stick against the ISO": reads the medium and compares it with the image without changing anything on it. A medium
/// written byte for byte is compared byte for byte up to the length of the image; a medium written in file mode is compared
/// file by file (size and SHA-256) against the files of the ISO. All targets are checked; the first failure is reported.
/// </summary>
public sealed class VerifyMediaJob(IDiskService disks, IImageStreamProvider images, ILogger<VerifyMediaJob> logger)
{
    public const string BytesKey = "verify.bytes";

    /// <summary>How much of the start of the image decides between a raw and a file-mode medium.</summary>
    private const int ProbeBytes = 34 * 1024;

    public IJob Create(VerifyMediaRequest request)
    {
        if (request.Targets.Count == 0)
        {
            throw new ArgumentException("At least one target disk is required.", nameof(request));
        }

        var run = new Run(disks, images, logger, request);
        return new Job(
            "verify-" + Guid.NewGuid().ToString("N")[..8],
            Path.GetFileName(request.ImagePath),
            [
                new DelegateJobStep("Verify.Check", 2, run.CheckAsync),
                new DelegateJobStep("Verify.Compare", 98, run.CompareAsync),
            ]);
    }

    private sealed class Run(IDiskService disks, IImageStreamProvider images, ILogger logger, VerifyMediaRequest request)
    {
        public Task CheckAsync(JobContext context, CancellationToken cancellationToken)
        {
            foreach (var target in request.Targets)
            {
                // Nothing is written, so a protected disk may be read; the mutex keeps a write job from running underneath the comparison.
                var mutex = DiskMutex.TryAcquire(target.Device)
                    ?? throw new BootrixException(ErrorCode.DeviceBusy, target.Device.DevicePath) { Arguments = ["Bootrix"] };
                context.OnCleanup(mutex);
                DiskIdentityReader.EnsureUnchanged(disks, target.Device, target.ConfirmedIdentity);
                cancellationToken.ThrowIfCancellationRequested();
            }

            return Task.CompletedTask;
        }

        public async Task CompareAsync(JobContext context, CancellationToken cancellationToken)
        {
            BootrixException? first = null;
            long compared = 0;
            for (var i = 0; i < request.Targets.Count; i++)
            {
                var target = request.Targets[i];
                var share = 1.0 / request.Targets.Count;
                var progress = new Progress<(long Done, long Total)>(p =>
                    context.ReportStep((i + (p.Total > 0 ? (double)p.Done / p.Total : 0)) * share, p.Done, p.Total));

                var mode = await ResolveModeAsync(target, cancellationToken).ConfigureAwait(false);
                logger.LogInformation("Verifying {Disk} in {Mode} mode", target.Device.DisplayName, mode);
                var (bytes, failure) = mode == VerifyMode.Raw
                    ? await CompareRawAsync(target, progress, cancellationToken).ConfigureAwait(false)
                    : await CompareFilesAsync(target, progress, cancellationToken).ConfigureAwait(false);
                compared += bytes;
                first ??= failure;
            }

            context.Set(BytesKey, compared);
            if (first is not null)
            {
                throw first;
            }
        }

        /// <summary>A medium whose first sectors are those of the image holds the image byte for byte; anything else was written in file mode.</summary>
        private async Task<VerifyMode> ResolveModeAsync(VerifyTargetRequest target, CancellationToken cancellationToken)
        {
            if (request.Mode != VerifyMode.Auto)
            {
                return request.Mode;
            }

            await using var image = await OpenImageAsync(cancellationToken).ConfigureAwait(false);
            var head = new byte[ProbeBytes];
            var read = await image.Stream.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);

            // Only ISO images have a file tree to fall back on; every other image can only have been written raw.
            if (image.Source?.Compression is not CompressionFormat.None || !LooksLikeIso(head, read))
            {
                return VerifyMode.Raw;
            }

            return await Task.Run(() => StartsWith(target, head, read), cancellationToken).ConfigureAwait(false) ? VerifyMode.Raw : VerifyMode.Files;
        }

        /// <summary>The ISO 9660 primary volume descriptor sits in sector 16 of 2048 bytes: type 1, then "CD001".</summary>
        private static bool LooksLikeIso(byte[] head, int read)
        {
            const int descriptor = 16 * 2048;
            return read >= descriptor + 6 && head[descriptor] == 1 && head.AsSpan(descriptor + 1, 5).SequenceEqual("CD001"u8);
        }

        private static bool StartsWith(VerifyTargetRequest target, byte[] expected, int expectedLength)
        {
            using var disk = DiskAccess.Open(target.Device, write: false);
            var length = (expectedLength + disk.SectorSize - 1) / disk.SectorSize * disk.SectorSize;
            using var buffer = new AlignedBuffer(length, disk.BufferAlignment);
            var read = disk.Read(0, buffer.GetSpan());
            return read >= expectedLength && buffer.GetSpan()[..expectedLength].SequenceEqual(expected.AsSpan(0, expectedLength));
        }

        private async Task<(long Bytes, BootrixException? Failure)> CompareRawAsync(
            VerifyTargetRequest target,
            IProgress<(long Done, long Total)> progress,
            CancellationToken cancellationToken)
        {
            await using var image = await OpenImageAsync(cancellationToken).ConfigureAwait(false);
            SparseWriteMap? sparse = image.Source is { BlockMap: { } map, FillBlockMapGaps: false } ? map.ToWriteMap(fillGaps: false) : null;
            using var disk = DiskAccess.Open(target.Device, write: false, cancellationToken);

            var report = await RawVerifier.CompareAsync(
                image.Stream,
                image.Length,
                disk,
                sparse,
                new Progress<RawWriteProgress>(p => progress.Report((p.BytesDone, p.BytesTotal))),
                cancellationToken: cancellationToken).ConfigureAwait(false);

            logger.LogInformation(
                "{Disk}: {Compared} bytes compared, {Differing} differing blocks",
                target.Device.DisplayName, report.BytesCompared, report.DifferingBlocks);
            return (report.BytesCompared, report.ToException(target.Device.DisplayName));
        }

        private async Task<(long Bytes, BootrixException? Failure)> CompareFilesAsync(
            VerifyTargetRequest target,
            IProgress<(long Done, long Total)> progress,
            CancellationToken cancellationToken)
        {
            // The volumes of the stick as they are now, not as they were when the job was asked for.
            var device = await Task.Run(() => disks.Find(target.Device.DevicePath), cancellationToken).ConfigureAwait(false)
                ?? throw new BootrixException(ErrorCode.DeviceNotFound, target.Device.DevicePath);
            var volume = device.Volumes
                .Where(v => !v.Unreadable && v.FileSystem is not null)
                .OrderByDescending(v => v.TotalBytes)
                .FirstOrDefault()
                ?? throw new BootrixException(ErrorCode.VolumeNotMounted, $"disk {device.DiskNumber} has no readable volume");

            await using var image = await images.OpenForInspectionAsync(request.ImagePath, new ImageOpenOptions { ArchiveEntry = request.ArchiveEntry }, cancellationToken).ConfigureAwait(false);
            var report = await Task.Run(() => FileTreeVerifier.Compare(image.Stream, volume.VolumeGuidPath, progress, cancellationToken), cancellationToken).ConfigureAwait(false);

            logger.LogInformation(
                "{Disk}: {Files} files compared, {Differences} differences ({Unexpected} unexpected), {Split} split files",
                target.Device.DisplayName, report.FilesCompared, report.Differences.Count, report.Unexpected.Count(), report.SplitFiles.Count);
            return (report.BytesCompared, report.ToException(target.Device.DisplayName));
        }

        private Task<OpenedImage> OpenImageAsync(CancellationToken cancellationToken) =>
            images.OpenAsync(request.ImagePath, new ImageOpenOptions { ArchiveEntry = request.ArchiveEntry, BlockMap = request.BlockMap }, cancellationToken);
    }
}
