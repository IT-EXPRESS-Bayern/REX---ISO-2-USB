// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Jobs;
using Bootrix.Core.Storage;
using Bootrix.Core.Writing.Raw;
using Bootrix.Windows.Interop;
using Bootrix.Windows.Platform;
using Bootrix.Windows.Storage;
using Microsoft.Extensions.Logging;

namespace Bootrix.Windows.Jobs;

/// <summary>One disk of a raw write; <paramref name="Persistence"/> adds a partition behind the image, <paramref name="RelocateBackupGpt"/> moves the image's backup GPT to the end of the disk.</summary>
public sealed record RawWriteTargetRequest(
    StorageDevice Device,
    DiskIdentity ConfirmedIdentity,
    PersistenceRequest? Persistence = null,
    bool RelocateBackupGpt = false);

public sealed record RawWriteRequest
{
    public required string ImagePath { get; init; }

    public required IReadOnlyList<RawWriteTargetRequest> Targets { get; init; }

    public bool Verify { get; init; } = true;

    public RawWriteOptions WriterOptions { get; init; } = new();

    /// <summary>The file inside a zip archive that holds the image; the archive's image when null.</summary>
    public string? ArchiveEntry { get; init; }

    /// <summary>What to do with a .bmap file next to the image.</summary>
    public BlockMapUse BlockMap { get; init; } = BlockMapUse.Auto;
}

/// <summary>
/// Writes an image byte for byte to one or several disks: hybrid ISOs, raw disk images, anything
/// that already contains its own partition table. Compressed images and Apple containers are decoded on the fly.
/// Before the first byte is written every target is checked against the identity the user confirmed, and
/// afterwards every target is read back. Once the image is verified, a persistence partition is added behind it
/// where one was asked for; the verification never sees it.
/// </summary>
public sealed class RawWriteJob
{
    public const string ReportKey = "raw.report";

    private readonly IDiskService _disks;
    private readonly IImageStreamProvider _images;
    private readonly JobJournal _journal;
    private readonly ILogger _logger;

    public RawWriteJob(IDiskService disks, IImageStreamProvider images, JobJournal journal, ILogger<RawWriteJob> logger)
    {
        _disks = disks;
        _images = images;
        _journal = journal;
        _logger = logger;
    }

    public IJob Create(RawWriteRequest request)
    {
        if (request.Targets.Count == 0)
        {
            throw new ArgumentException("At least one target disk is required.", nameof(request));
        }

        var run = new Run(this, request);
        var steps = new List<IJobStep>
        {
            new DelegateJobStep("Raw.CheckTargets", 2, run.CheckTargetsAsync),
            new DelegateJobStep("Raw.Prepare", 3, run.PrepareAsync),
            new DelegateJobStep("Raw.Write", 90, run.WriteAsync),
        };

        if (request.Targets.Any(target => target.Persistence is not null))
        {
            steps.Add(new DelegateJobStep("Raw.Persistence", 4, run.PersistenceAsync));
        }

        if (request.Targets.Any(target => target.RelocateBackupGpt && target.Persistence is null))
        {
            steps.Add(new DelegateJobStep("Raw.RelocateGpt", 1, run.RelocateGptAsync));
        }

        steps.Add(new DelegateJobStep("Raw.Finish", 5, run.FinishAsync));
        return new Job("raw-" + Guid.NewGuid().ToString("N")[..8], Path.GetFileName(request.ImagePath), steps);
    }

    private sealed class Run(RawWriteJob owner, RawWriteRequest request)
    {
        private readonly List<PhysicalDisk> _opened = [];
        private OpenedImage? _image;
        private string _jobId = "";

        public async Task CheckTargetsAsync(JobContext context, CancellationToken cancellationToken)
        {
            _jobId = context.Job.Id;
            _image = await owner._images.OpenAsync(
                request.ImagePath,
                new ImageOpenOptions { ArchiveEntry = request.ArchiveEntry, BlockMap = request.BlockMap },
                cancellationToken).ConfigureAwait(false);
            context.OnCleanup(_image.DisposeAsync);

            foreach (var target in request.Targets)
            {
                var device = target.Device;
                if (device.IsBlocked)
                {
                    throw new BootrixException(ErrorCode.DeviceProtected, device.DevicePath) { Arguments = [device.Protection.ToString()] };
                }

                var mutex = DiskMutex.TryAcquire(device)
                    ?? throw new BootrixException(ErrorCode.DeviceBusy, device.DevicePath) { Arguments = ["Bootrix"] };
                context.OnCleanup(mutex);

                if (_image.Length is { } length && length > device.SizeBytes)
                {
                    throw new BootrixException(ErrorCode.DeviceTooSmall, device.DevicePath)
                    {
                        Arguments = [FormatSize(length), FormatSize(device.SizeBytes)],
                    };
                }

                await Task.Run(() => DiskIdentityReader.EnsureUnchanged(owner._disks, device, target.ConfirmedIdentity), cancellationToken).ConfigureAwait(false);
            }

            var sleep = new SleepGuard();
            context.OnCleanup(sleep);
        }

        public async Task PrepareAsync(JobContext context, CancellationToken cancellationToken)
        {
            await owner._journal.WriteAsync(
                new JournalEntry
                {
                    JobId = _jobId,
                    Kind = "WriteImage",
                    DiskIdentity = string.Join(';', request.Targets.Select(t => t.ConfirmedIdentity.ToKey())),
                    Phase = "prepare",
                    SourcePath = request.ImagePath,
                    StartedUtc = DateTimeOffset.UtcNow,
                },
                cancellationToken).ConfigureAwait(false);

            await Task.Run(
                () =>
                {
                    foreach (var target in request.Targets)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        // The locks stay in place until the job ends; a lock only holds while its handle is open.
                        var locks = VolumeLockSet.Acquire(target.Device, owner._logger, cancellationToken);
                        context.OnCleanup(locks);

                        var disk = DiskAccess.Open(target.Device, write: true, cancellationToken);
                        _opened.Add(disk);
                        context.OnCleanup(disk);

                        // Kills stale partition tables and the old backup GPT at the end of the disk.
                        DiskWiper.WipeTables(disk);
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }

        public async Task WriteAsync(JobContext context, CancellationToken cancellationToken)
        {
            var progress = new Progress<RawWriteProgress>(p =>
                context.ReportBytes(p.BytesDone, p.BytesTotal, p.Phase == RawWritePhase.Verifying ? "verify" : null));

            var options = request.WriterOptions with { Verify = request.Verify };
            using var verifier = ApplyBlockMap(ref options);
            var report = await new RawImageWriter().WriteAsync(
                _image!.Stream,
                _image.Length,
                _opened,
                options,
                progress,
                cancellationToken).ConfigureAwait(false);

            context.Set(ReportKey, report);
            if (!report.AllSucceeded)
            {
                var failed = report.Targets.First(t => !t.Succeeded);
                throw failed.Error is BootrixException known
                    ? known
                    : new BootrixException(ErrorCode.DeviceRemoved, failed.Device.Name, failed.Error);
            }

            // A map that does not fit the image would have skipped the wrong blocks; the range checksums have caught that by now.
            verifier?.Complete(report.ImageBytes);
            owner._logger.LogInformation("Image written to {Count} disk(s), SHA-256 {Hash}", report.Targets.Count, report.Sha256);
        }

        public async Task PersistenceAsync(JobContext context, CancellationToken cancellationToken)
        {
            await Task.Run(
                () =>
                {
                    for (var i = 0; i < request.Targets.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (request.Targets[i].Persistence is not { } persistence)
                        {
                            continue;
                        }

                        var result = PersistenceInstaller.Install(_opened[i], persistence, cancellationToken);
                        owner._logger.LogInformation(
                            "Persistence partition '{Label}' of {Length} bytes added at {Start} ({Table} slot {Slot})",
                            result.Label, result.LengthBytes, result.StartBytes, result.Table, result.Slot + 1);
                        context.ReportStep((i + 1d) / request.Targets.Count);
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }

        public async Task RelocateGptAsync(JobContext context, CancellationToken cancellationToken)
        {
            await Task.Run(
                () =>
                {
                    for (var i = 0; i < request.Targets.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (request.Targets[i].RelocateBackupGpt && request.Targets[i].Persistence is null)
                        {
                            owner._logger.LogInformation("Backup GPT of {Disk}: {Result}", request.Targets[i].Device.DisplayName, GptRelocation.MoveToDiskEnd(_opened[i]));
                        }
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }

        public async Task FinishAsync(JobContext context, CancellationToken cancellationToken)
        {
            await Task.Run(
                () =>
                {
                    foreach (var disk in _opened)
                    {
                        disk.Flush();

                        // Make Windows read the partition table the image brought along.
                        DeviceIo.TryControl(disk.Handle, Ioctl.DiskUpdateProperties);
                    }
                },
                cancellationToken).ConfigureAwait(false);

            await owner._journal.MarkCompletedAsync(
                new JournalEntry { JobId = _jobId, Kind = "WriteImage", Phase = "done", SourcePath = request.ImagePath },
                cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Writes only what the block map next to the image lists, and checks the image against the map's checksums on the way.</summary>
        private BlockMapVerifier? ApplyBlockMap(ref RawWriteOptions options)
        {
            var source = _image!.Source;
            if (source?.BlockMapSkipped is { } skipped)
            {
                owner._logger.LogWarning("Block map not used: {Reason}", skipped);
            }

            if (source?.BlockMap is not { } map)
            {
                return null;
            }

            var verifier = BlockMapVerifier.Create(map);
            options = options with
            {
                Sparse = map.ToWriteMap(source.FillBlockMapGaps),
                SourceObserver = verifier is null ? null : verifier.Observe,
            };
            owner._logger.LogInformation(
                "Writing by block map: {Mapped} of {Size} bytes, gaps {Gaps}",
                map.MappedBytes, map.ImageSize, source.FillBlockMapGaps ? "filled with zeros" : "left alone");
            return verifier;
        }

        private static string FormatSize(long bytes) => bytes >= 1L << 30
            ? $"{bytes / (double)(1L << 30):0.#} GB"
            : $"{bytes / (double)(1L << 20):0.#} MB";
    }
}
