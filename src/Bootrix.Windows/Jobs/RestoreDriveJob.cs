// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using Bootrix.Core.Errors;
using Bootrix.Core.Jobs;
using Bootrix.Core.Planning;
using Bootrix.Core.Storage;
using Bootrix.Core.Writing.Restore;
using Bootrix.Windows.FileSystems;
using Bootrix.Windows.Interop;
using Bootrix.Windows.Platform;
using Bootrix.Windows.Storage;
using Bootrix.Windows.Writing;
using Microsoft.Extensions.Logging;

namespace Bootrix.Windows.Jobs;

/// <summary>One disk to restore, with the plan <see cref="RestorePlanner"/> made for exactly this device.</summary>
public sealed record RestoreTargetRequest(StorageDevice Device, DiskIdentity ConfirmedIdentity, MediaPlan Plan);

/// <summary>
/// Brings sticks back to a clean state, typically after a hybrid image: the old tables and the start and end of the
/// disk are erased (including a backup GPT that sits behind the old image), then one partition over the whole
/// disk is created and formatted. Independent of the media writers: there is no image.
/// </summary>
public sealed class RestoreDriveJob(IDiskService disks, DiskPreparer preparer, JobJournal journal, ILogger<RestoreDriveJob> logger)
{
    private const string Kind = "RestoreDrive";

    public IJob Create(IReadOnlyList<RestoreTargetRequest> targets)
    {
        if (targets.Count == 0)
        {
            throw new ArgumentException("At least one target disk is required.", nameof(targets));
        }

        var run = new Run(disks, preparer, journal, logger, targets);
        return new Job(
            "restore-" + Guid.NewGuid().ToString("N")[..8],
            targets.Count == 1 ? targets[0].Device.DisplayName : $"{targets.Count} drives",
            [
                new DelegateJobStep("Restore.Check", 2, run.CheckAsync),
                new DelegateJobStep("Restore.Wipe", 18, run.WipeAsync),
                new DelegateJobStep("Restore.Format", 72, run.FormatAsync),
                new DelegateJobStep("Restore.Finish", 8, run.FinishAsync),
            ]);
    }

    private sealed class Run(
        IDiskService disks,
        DiskPreparer preparer,
        JobJournal journal,
        ILogger logger,
        IReadOnlyList<RestoreTargetRequest> targets)
    {
        private readonly Dictionary<RestoreTargetRequest, PreparedDisk> _prepared = [];
        private string _jobId = "";

        public Task CheckAsync(JobContext context, CancellationToken cancellationToken)
        {
            _jobId = context.Job.Id;
            foreach (var target in targets)
            {
                var device = target.Device;
                if (device.IsBlocked)
                {
                    throw new BootrixException(ErrorCode.DeviceProtected, device.DevicePath) { Arguments = [device.Protection.ToString()] };
                }

                var mutex = DiskMutex.TryAcquire(device)
                    ?? throw new BootrixException(ErrorCode.DeviceBusy, device.DevicePath) { Arguments = ["Bootrix"] };
                context.OnCleanup(mutex);

                DiskIdentityReader.EnsureUnchanged(disks, device, target.ConfirmedIdentity);
                cancellationToken.ThrowIfCancellationRequested();
            }

            context.OnCleanup(new SleepGuard());
            return Task.CompletedTask;
        }

        public async Task WipeAsync(JobContext context, CancellationToken cancellationToken)
        {
            foreach (var target in targets)
            {
                await journal.WriteAsync(
                    new JournalEntry
                    {
                        JobId = _jobId,
                        Kind = Kind,
                        DiskIdentity = target.ConfirmedIdentity.ToKey(),
                        Phase = "wipe",
                        StartedUtc = DateTimeOffset.UtcNow,
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            await Task.Run(
                () =>
                {
                    foreach (var target in targets)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        // Released at the end of the step: the partitioning below takes the locks again for the new volumes.
                        using var locks = VolumeLockSet.Acquire(target.Device, logger, cancellationToken);
                        using var disk = DiskAccess.Open(target.Device, write: true, cancellationToken);
                        var extra = DriveWiper.Wipe(disk, cancellationToken);
                        disk.Flush();
                        DeviceIo.TryControl(disk.Handle, Ioctl.DiskUpdateProperties);
                        logger.LogInformation("Wiped {Disk}, {Extra} extra range(s) behind the old image", target.Device.DisplayName, extra.Count);
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }

        public async Task FormatAsync(JobContext context, CancellationToken cancellationToken)
        {
            for (var i = 0; i < targets.Count; i++)
            {
                var target = targets[i];
                var layout = PlanLayout.ToLayoutSpec(target.Plan, (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue), Guid.NewGuid());

                // FAT is written by Bootrix before Windows sees the partition; NTFS and exFAT are formatted by Windows once it exists.
                var prepared = await preparer.PrepareAsync(
                    new PrepareRequest(target.Device, layout, PlanLayout.FatPayloads(target.Plan), PlanLayout.MountedPartitions(target.Plan)),
                    cancellationToken).ConfigureAwait(false);
                _prepared[target] = prepared;

                await FormatWindowsFileSystemsAsync(target, prepared, context, i, cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task FinishAsync(JobContext context, CancellationToken cancellationToken)
        {
            await Task.Run(
                () =>
                {
                    foreach (var target in targets)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        foreach (var partition in _prepared.GetValueOrDefault(target)?.Partitions ?? [])
                        {
                            if (partition.Volume is { } volume)
                            {
                                VolumeFlusher.Flush(volume.VolumeGuidPath);
                            }
                        }

                        using var disk = DiskAccess.Open(target.Device, write: true, cancellationToken);
                        disk.Flush();
                        DeviceIo.TryControl(disk.Handle, Ioctl.DiskUpdateProperties);
                    }
                },
                cancellationToken).ConfigureAwait(false);

            foreach (var target in targets)
            {
                await journal.MarkCompletedAsync(
                    new JournalEntry { JobId = _jobId, Kind = Kind, DiskIdentity = target.ConfirmedIdentity.ToKey(), Phase = "done" },
                    cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task FormatWindowsFileSystemsAsync(RestoreTargetRequest target, PreparedDisk prepared, JobContext context, int index, CancellationToken cancellationToken)
        {
            for (var partitionIndex = 0; partitionIndex < target.Plan.Partitions.Count; partitionIndex++)
            {
                var partition = target.Plan.Partitions[partitionIndex];
                if (!PlanLayout.NeedsWindowsFormat(partition))
                {
                    continue;
                }

                var volume = prepared.Partitions[partitionIndex].Volume
                    ?? throw new BootrixException(ErrorCode.VolumeNotMounted, $"partition {partitionIndex} of disk {target.Device.DiskNumber}");

                var share = 1.0 / targets.Count;
                var progress = new Progress<double>(value => context.ReportStep((index + value) * share));
                await FmifsFormatter.FormatAsync(
                    new FormatRequest(
                        volume.VolumeGuidPath,
                        partition.FileSystem!.Value,
                        partition.Label,
                        partition.ClusterSizeBytes ?? 0,
                        Quick: true,
                        target.Device.IsRemovableMedia),
                    progress,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
