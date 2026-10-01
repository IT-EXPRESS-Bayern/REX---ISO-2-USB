// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Images.Policy;
using Bootrix.Core.Jobs;
using Bootrix.Core.Planning;
using Bootrix.Core.Storage;
using Bootrix.Core.Writing.Linux;
using Bootrix.Windows.Jobs;
using Bootrix.Windows.Storage;
using Microsoft.Extensions.Logging;

namespace Bootrix.Windows.Writing.Linux;

/// <summary>
/// The state one Linux write shares between its steps: what the builder put on each target and which BIOS loader it
/// decided on. The steps are separate so that progress, cancellation and the journal see them one by one.
/// </summary>
internal sealed class LinuxWriteRun(WriteServices services, IImageStreamProvider images, MediaWriteContext write)
{
    private readonly Dictionary<MediaWriteTarget, LinuxBuildResult> _results = [];
    private ILogger? _logger;

    private ILogger Log => _logger ??= services.LoggerFor<LinuxWriteRun>();

    /// <summary>
    /// Partitions that Bootrix fills before Windows sees the new layout: the persistence store (ext3, which Windows
    /// cannot format) and the UEFI:NTFS helper behind an NTFS or exFAT main partition.
    /// </summary>
    public IReadOnlyList<PartitionPayload> ExtraPayloads(MediaWriteTarget target)
    {
        var traits = ImagePolicy.Default.TraitsOf(write.Image.Family);
        var payloads = new List<PartitionPayload>();
        for (var index = 0; index < target.Plan.Partitions.Count; index++)
        {
            var partition = target.Plan.Partitions[index];
            switch (partition.Role)
            {
                case PartitionRole.Persistence when partition.FileSystem == Core.Model.FileSystemKind.Ext3:
                    payloads.Add(new PartitionPayload(index, stream => PersistenceStore.Format(stream, partition, traits)));
                    break;
                case PartitionRole.UefiNtfs:
                    payloads.Add(new PartitionPayload(index, UefiNtfsHelper.Write));
                    break;
            }
        }

        return payloads;
    }

    public async Task CopyAsync(JobContext context, CancellationToken cancellationToken)
    {
        await using var image = await images.OpenAsync(write.ImagePath, cancellationToken).ConfigureAwait(false);
        using var iso = await Task.Run(() => IsoContent.Open(image.Stream, cancellationToken), cancellationToken).ConfigureAwait(false);
        var facts = LinuxTreeFacts.Scan(iso);

        var total = iso.TotalBytes * write.Targets.Count;
        long before = 0;
        foreach (var target in write.Targets)
        {
            var volume = MainVolume(target);
            var settings = LinuxBuildPlanner.Create(target.Plan, write.Image, facts);
            var builder = new LinuxMediaBuilder(services.LoggerFor<LinuxMediaBuilder>());
            var offset = before;
            var progress = new InlineProgress(p => context.ReportBytes(offset + p.BytesDone, total, p.Path));

            var result = await Task.Run(
                () => builder.Build(iso, new DirectoryVolume(volume.VolumeGuidPath), settings, progress, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            LogNotices(result);
            _results[target] = result;
            before += iso.TotalBytes;
            await Task.Run(() => VolumeFlusher.Flush(volume.VolumeGuidPath), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes the BIOS boot code. The volumes are locked and dismounted first: the loaders address sectors of the
    /// disk, which Windows only permits while no file system is mounted on them. Unlocking lets Windows mount the
    /// volumes again from what is really on the device, which is what the verification step then reads.
    /// </summary>
    public async Task InstallBootloaderAsync(JobContext context, CancellationToken cancellationToken)
    {
        foreach (var target in write.Targets)
        {
            var result = _results[target];
            var device = services.Disks.Find(target.Device.DevicePath) ?? target.Device;
            await Task.Run(
                () =>
                {
                    using var locks = VolumeLockSet.Acquire(device, Log, cancellationToken);
                    if (result.Bios.Loader == BiosLoader.None)
                    {
                        return;
                    }

                    using var disk = DiskAccess.Open(device, write: true, cancellationToken);
                    using var stream = new BlockDeviceStream(disk, 0, disk.Length);
                    LinuxBootCodeInstaller.Install(stream, target.Plan, result.Bios);
                    stream.Flush();
                    Log.LogInformation("Installed the {Loader} boot code on disk {Disk}", result.Bios.Loader, device.DiskNumber);
                },
                cancellationToken).ConfigureAwait(false);
        }

        context.ReportStep(1);
    }

    public async Task VerifyAsync(JobContext context, CancellationToken cancellationToken)
    {
        await using var image = await images.OpenAsync(write.ImagePath, cancellationToken).ConfigureAwait(false);
        using var iso = await Task.Run(() => IsoContent.Open(image.Stream, cancellationToken), cancellationToken).ConfigureAwait(false);

        var total = _results.Values.Sum(result => result.Files.Sum(file => file.Length));
        long before = 0;
        foreach (var target in write.Targets)
        {
            var result = _results[target];
            var volume = MainVolume(target);
            var offset = before;
            var progress = new InlineProgress(p => context.ReportBytes(offset + p.BytesDone, total, p.Path));

            await Task.Run(() => LinuxMediaVerifier.Verify(iso, new DirectoryVolume(volume.VolumeGuidPath), result, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
            before += result.Files.Sum(file => file.Length);

            var device = services.Disks.Find(target.Device.DevicePath) ?? target.Device;
            await Task.Run(() => VerifyBootCode(device, target, result, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
    }

    private static void VerifyBootCode(StorageDevice device, MediaWriteTarget target, LinuxBuildResult result, CancellationToken cancellationToken)
    {
        if (result.Bios.Loader == BiosLoader.None)
        {
            return;
        }

        using var disk = DiskAccess.Open(device, write: false, cancellationToken);
        using var stream = new BlockDeviceStream(disk, 0, disk.Length);
        if (LinuxBootCodeInstaller.Verify(stream, target.Plan, result.Bios) is { } problem)
        {
            throw new BootrixException(ErrorCode.BootloaderInstallFailed, problem) { Arguments = [result.Bios.Loader.ToString(), problem] };
        }
    }

    private static VolumeInfo MainVolume(MediaWriteTarget target)
    {
        var index = target.Plan.Partitions.ToList().FindIndex(p => p.Role == PartitionRole.Main);
        return target.Prepared?.Partitions[index].Volume
            ?? throw new BootrixException(ErrorCode.VolumeNotMounted, $"main partition of disk {target.Device.DiskNumber}");
    }

    private void LogNotices(LinuxBuildResult result)
    {
        foreach (var notice in result.Notices)
        {
            var level = notice.Severity switch
            {
                Core.Images.WarningSeverity.Warning => LogLevel.Warning,
                Core.Images.WarningSeverity.Error => LogLevel.Error,
                _ => LogLevel.Information,
            };
            Log.Log(level, "{Notice}", notice.Format());
        }

        Log.LogInformation(
            "Copied {Files} files, {Bytes} bytes; BIOS loader {Loader}; {Patches} configuration file(s) adapted",
            result.Files.Count,
            result.Bytes,
            result.Bios.Loader,
            result.Patches.Count);
    }

    /// <summary>Reports on the thread that produced the value; <see cref="Progress{T}"/> would queue the reports and let them overtake the end of the step.</summary>
    private sealed class InlineProgress(Action<LinuxCopyProgress> handler) : IProgress<LinuxCopyProgress>
    {
        public void Report(LinuxCopyProgress value) => handler(value);
    }
}
