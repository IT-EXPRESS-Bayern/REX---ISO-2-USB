// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using Bootrix.Core.Errors;
using Bootrix.Core.FileSystems.Fat;
using Bootrix.Core.Jobs;
using Bootrix.Core.Model;
using Bootrix.Windows.FileSystems;
using Bootrix.Windows.Interop;
using Bootrix.Windows.Platform;
using Bootrix.Windows.Storage;

namespace Bootrix.Windows.Writing;

/// <summary>
/// The steps that every writer which partitions the disk needs, in the same order and with the same
/// guarantees: targets are checked before the first byte is written, the layout is applied before Windows
/// can ask to format anything, and the data is flushed before the job reports success.
/// </summary>
public static class StandardSteps
{
    public const string CheckKey = "Write.CheckTargets";
    public const string PrepareKey = "Write.Prepare";
    public const string FinishKey = "Write.Finish";

    /// <summary>Protection, disk lock, fingerprint, sleep guard. Must come before anything that touches a disk.</summary>
    public static IJobStep Check(WriteServices services, MediaWriteContext write, double weight = 2) =>
        new DelegateJobStep(CheckKey, weight, (context, ct) => CheckAsync(services, write, context, ct));

    /// <summary>
    /// Partitions the disk, formats FAT in-process and everything else through Windows once the volume
    /// exists, and leaves <see cref="MediaWriteTarget.Prepared"/> filled in for the steps that follow.
    /// </summary>
    public static IJobStep Prepare(
        WriteServices services,
        MediaWriteContext write,
        Func<Core.Planning.PlannedPartition, FatFormatOptions, FatFormatOptions>? customizeFat = null,
        double weight = 6,
        Func<MediaWriteTarget, IReadOnlyList<PartitionPayload>>? extraPayloads = null) =>
        new DelegateJobStep(PrepareKey, weight, (context, ct) => PrepareAsync(services, write, customizeFat, extraPayloads, context, ct));

    /// <summary>Flushes every volume, refreshes the partition tables and closes the journal entry.</summary>
    public static IJobStep Finish(WriteServices services, MediaWriteContext write, double weight = 4) =>
        new DelegateJobStep(FinishKey, weight, (context, ct) => FinishAsync(services, write, ct));

    private static Task CheckAsync(WriteServices services, MediaWriteContext write, JobContext context, CancellationToken cancellationToken)
    {
        foreach (var target in write.Targets)
        {
            var device = target.Device;
            if (device.IsBlocked)
            {
                throw new BootrixException(ErrorCode.DeviceProtected, device.DevicePath) { Arguments = [device.Protection.ToString()] };
            }

            var mutex = DiskMutex.TryAcquire(device)
                ?? throw new BootrixException(ErrorCode.DeviceBusy, device.DevicePath) { Arguments = ["Bootrix"] };
            context.OnCleanup(mutex);

            DiskIdentityReader.EnsureUnchanged(services.Disks, device, target.Identity);
            cancellationToken.ThrowIfCancellationRequested();
        }

        context.OnCleanup(new SleepGuard());
        return Task.CompletedTask;
    }

    private static async Task PrepareAsync(
        WriteServices services,
        MediaWriteContext write,
        Func<Core.Planning.PlannedPartition, FatFormatOptions, FatFormatOptions>? customizeFat,
        Func<MediaWriteTarget, IReadOnlyList<PartitionPayload>>? extraPayloads,
        JobContext context,
        CancellationToken cancellationToken)
    {
        var count = write.Targets.Count;
        for (var i = 0; i < count; i++)
        {
            var target = write.Targets[i];
            await services.Journal.WriteAsync(
                new JournalEntry
                {
                    JobId = write.JobId,
                    Kind = "WriteImage",
                    DiskIdentity = target.Identity.ToKey(),
                    Phase = "prepare",
                    SourcePath = write.ImagePath,
                    StartedUtc = DateTimeOffset.UtcNow,
                },
                cancellationToken).ConfigureAwait(false);

            var layout = PlanLayout.ToLayoutSpec(
                target.Plan,
                (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue),
                Guid.NewGuid());

            target.Prepared = await services.Preparer.PrepareAsync(
                new PrepareRequest(
                    target.Device,
                    layout,
                    [.. PlanLayout.FatPayloads(target.Plan, customizeFat), .. extraPayloads?.Invoke(target) ?? []],
                    PlanLayout.MountedPartitions(target.Plan)),
                cancellationToken).ConfigureAwait(false);

            await FormatWindowsFileSystemsAsync(target, context, i, count, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task FormatWindowsFileSystemsAsync(MediaWriteTarget target, JobContext context, int targetIndex, int targetCount, CancellationToken cancellationToken)
    {
        var plan = target.Plan;
        for (var index = 0; index < plan.Partitions.Count; index++)
        {
            var partition = plan.Partitions[index];
            if (!PlanLayout.NeedsWindowsFormat(partition))
            {
                continue;
            }

            var volume = target.Prepared!.Partitions[index].Volume
                ?? throw new BootrixException(ErrorCode.VolumeNotMounted, $"partition {index} of disk {target.Device.DiskNumber}");

            var share = 1.0 / targetCount;
            var progress = new Progress<double>(value => context.ReportStep((targetIndex + value) * share));
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

    private static async Task FinishAsync(WriteServices services, MediaWriteContext write, CancellationToken cancellationToken)
    {
        await Task.Run(
            () =>
            {
                foreach (var target in write.Targets)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (var partition in target.Prepared?.Partitions ?? [])
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

        foreach (var target in write.Targets)
        {
            await services.Journal.MarkCompletedAsync(
                new JournalEntry
                {
                    JobId = write.JobId,
                    Kind = "WriteImage",
                    DiskIdentity = target.Identity.ToKey(),
                    Phase = "done",
                    SourcePath = write.ImagePath,
                },
                cancellationToken).ConfigureAwait(false);
        }
    }
}
