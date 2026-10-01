// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.FileSystems.Fat;
using Bootrix.Core.Jobs;
using Bootrix.Core.Model;
using Bootrix.Core.Planning;
using Bootrix.Core.Storage;
using Bootrix.Core.Text;
using Bootrix.Core.Wim;
using Bootrix.Core.Writing.Windows;
using Bootrix.Windows.Jobs;
using Bootrix.Windows.Storage;
using Microsoft.Extensions.Logging;

namespace Bootrix.Windows.Writing.Windows;

/// <summary>
/// What the steps of one Windows setup write share: the source of the files, the copy plan and the result of the
/// copy for every target. The steps are separate so that progress, cancellation and the journal see them one by one.
/// </summary>
internal sealed class WindowsSetupRun(
    WriteServices services,
    IImageStreamProvider images,
    IVbrCodeSource bootCode,
    IReadOnlyList<IWindowsMediaCustomizer> customizers,
    MediaWriteContext write)
{
    private readonly Dictionary<MediaWriteTarget, TargetRun> _targets = [];
    private readonly Dictionary<int, byte[]> _uefiNtfs = [];
    private IWindowsMediaSource? _source;
    private FatBootSectors? _fatBootCode;
    private ILogger? _logger;

    private ILogger Log => _logger ??= services.LoggerFor<WindowsSetupRun>();

    private string ScratchDirectory => Path.Combine(write.WorkDirectory, "scratch");

    /// <summary>Opens the image, plans the copy for every target and gets the boot code; fails here, before any disk is touched, if something is missing.</summary>
    public async Task OpenSourceAsync(JobContext context, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(write.WorkDirectory);
        context.OnCleanup(() => DeleteWorkDirectory(write.WorkDirectory));

        _source = await WindowsSourceSession.OpenAsync(images, write.ImagePath, write.Inspection, context, Log, cancellationToken).ConfigureAwait(false);

        foreach (var target in write.Targets)
        {
            var run = await Task.Run(() => Plan(target), cancellationToken).ConfigureAwait(false);
            _targets[target] = run;

            foreach (var finding in BootArchitectureCheck.Findings(write.Inspection, target.Plan))
            {
                Log.LogWarning("{Finding}", finding);
            }

            if (NeedsFatBootCode(target.Plan) && _fatBootCode is null)
            {
                _fatBootCode = await bootCode.ReadFat32Async(Path.Combine(write.WorkDirectory, "bootcode"), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Boot code of the NT 6 FAT32 boot sector for the BIOS start; everything else about the volume stays as planned.</summary>
    public FatFormatOptions CustomizeFat(PlannedPartition partition, FatFormatOptions options) =>
        partition.Role == PartitionRole.Main
            && _fatBootCode is { } code
            && write.Targets.Any(target => NeedsFatBootCode(target.Plan) && target.Plan.Partitions.Contains(partition))
                ? WindowsFatBootCode.Apply(options, code)
                : options;

    /// <summary>The UEFI:NTFS helper is written before Windows sees the layout, so no volume can exist on that range of the disk.</summary>
    public IReadOnlyList<PartitionPayload> ExtraPayloads(MediaWriteTarget target)
    {
        var payloads = new List<PartitionPayload>();
        for (var index = 0; index < target.Plan.Partitions.Count; index++)
        {
            if (target.Plan.Partitions[index].Role != PartitionRole.UefiNtfs)
            {
                continue;
            }

            var image = UefiNtfsFor(target.Plan.SectorSize);
            payloads.Add(new PartitionPayload(index, stream => stream.Write(image)));
        }

        return payloads;
    }

    public async Task CopyAsync(JobContext context, CancellationToken cancellationToken)
    {
        var verify = write.Spec.Verify.ReadBack;
        var share = new TargetSequenceProgress(write.Targets.Count, _targets.Values.Max(run => run.CopyPlan.TotalBytes));
        for (var index = 0; index < write.Targets.Count; index++)
        {
            var target = write.Targets[index];
            var run = _targets[target];
            var root = MainVolume(target).VolumeGuidPath;
            var targetIndex = index;
            var progress = new InlineProgress<CopyProgress>(p => context.ReportBytes(
                share.Overall(targetIndex, p.BytesDone, run.CopyPlan.TotalBytes), share.TotalBytes, p.File));

            Log.LogInformation("Copying {Files} files ({Bytes} bytes) to {Volume}", run.CopyPlan.Items.Count, run.CopyPlan.TotalBytes, root);
            var copier = new WindowsMediaCopier(_source!, ScratchDirectory, services.LoggerFor<WindowsMediaCopier>());
            try
            {
                run.Copied = await copier.CopyAsync(run.CopyPlan, root, computeHashes: verify, progress, cancellationToken).ConfigureAwait(false);
            }
            catch (IOException ex) when (IsDiskFull(ex))
            {
                throw new BootrixException(ErrorCode.ImageTooLarge, ex.Message, ex)
                {
                    Arguments = [ByteSize.Format(run.CopyPlan.TotalBytes), ByteSize.Format(MainPartition(target).LengthBytes)],
                };
            }

            await Task.Run(() => VolumeFlusher.Flush(root), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task WriteBootCodeAsync(JobContext context, CancellationToken cancellationToken)
    {
        foreach (var target in write.Targets.Where(target => WindowsMbr.IsNeeded(target.Plan)))
        {
            var device = services.Disks.Find(target.Device.DevicePath) ?? target.Device;
            await Task.Run(() => TargetBootRecords.WriteMbr(device, target.Plan, Log, cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        context.ReportStep(1);
    }

    /// <summary>
    /// Reads everything back from the device: the volume is dismounted first, so Windows has to read the files again
    /// instead of answering from its cache. Runs before the customizers, which rewrite files (boot.wim, the boot
    /// manager) and would otherwise have to be told apart from a damaged copy.
    /// </summary>
    public async Task VerifyAsync(JobContext context, CancellationToken cancellationToken)
    {
        var share = new TargetSequenceProgress(write.Targets.Count, _targets.Values.Max(run => run.Copied.Sum(file => file.Length)));
        for (var index = 0; index < write.Targets.Count; index++)
        {
            var target = write.Targets[index];
            var run = _targets[target];
            var device = services.Disks.Find(target.Device.DevicePath) ?? target.Device;
            var root = MainVolume(target).VolumeGuidPath;
            var targetIndex = index;
            var bytes = run.Copied.Sum(file => file.Length);

            await Task.Run(() => DropCaches(target, device, cancellationToken), cancellationToken).ConfigureAwait(false);

            var progress = new InlineProgress<long>(done => context.ReportBytes(share.Overall(targetIndex, done, bytes), share.TotalBytes));
            var result = await MediaVerifier.VerifyAsync(root, run.Copied, progress, cancellationToken).ConfigureAwait(false);
            Log.LogInformation("Verified {Files} files, {Bytes} bytes on disk {Disk}", result.Files, result.Bytes, device.DiskNumber);

            await Task.Run(() => VerifyRecords(target, device, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task CustomizeAsync(JobContext context, CancellationToken cancellationToken)
    {
        var parts = write.Targets.Count * customizers.Count;
        var done = 0;
        for (var index = 0; index < write.Targets.Count; index++)
        {
            var target = write.Targets[index];
            var root = MainVolume(target).VolumeGuidPath;
            foreach (var customizer in customizers)
            {
                var folder = Path.Combine(write.WorkDirectory, "customize", $"{index}-{customizer.Id}");
                Directory.CreateDirectory(folder);
                var customization = new WindowsMediaCustomization
                {
                    Write = write,
                    Target = target,
                    MediaRoot = root,
                    Arch = write.Image.Arch,
                    Build = write.Image.WindowsBuild,
                    WorkDirectory = folder,
                };

                var before = done;
                var progress = new InlineProgress<double>(value => context.ReportStep((before + Math.Clamp(value, 0, 1)) / parts, detail: customizer.Id));
                Log.LogInformation("Applying {Customizer} to {Volume}", customizer.Id, root);
                await customizer.ApplyAsync(customization, progress, cancellationToken).ConfigureAwait(false);
                done++;
            }
        }

        context.ReportStep(1);
    }

    private TargetRun Plan(MediaWriteTarget target)
    {
        var plan = target.Plan;
        var main = MainPartition(target);
        var fat = main.FileSystem is FileSystemKind.Fat12 or FileSystemKind.Fat16 or FileSystemKind.Fat32;
        var options = new WindowsCopyOptions
        {
            MaxFileBytes = fat ? WindowsCopyOptions.Fat32MaxFileBytes : null,
            FileSystemName = main.FileSystem?.ToString().ToUpperInvariant() ?? "FAT32",
            SplitInstallImage = plan.SplitWim,
        };

        var copyPlan = WindowsCopyPlan.Create(_source!, options);
        if (copyPlan.HasSplit && !WimFile.IsAvailable)
        {
            throw new BootrixException(ErrorCode.WimLibraryMissing, "wimlib could not be loaded");
        }

        EnsureRoom(target, main, fat, copyPlan);
        return new TargetRun(copyPlan);
    }

    /// <summary>The planner sized the partition for the image; this checks it for the files that will really be written, in whole clusters on FAT.</summary>
    private static void EnsureRoom(MediaWriteTarget target, PlannedPartition main, bool fat, WindowsCopyPlan copyPlan)
    {
        long needed;
        long capacity;
        if (fat)
        {
            var layout = FatGeometry.Compute(target.Plan.ToFatOptions(main));
            needed = copyPlan.BytesOnFat((int)layout.ClusterBytes);
            capacity = layout.ClusterCount * layout.ClusterBytes;
        }
        else
        {
            // NTFS and exFAT keep their tables in the volume; the margin is the one the planner used.
            needed = copyPlan.TotalBytes + Math.Max(32L << 20, copyPlan.TotalBytes / 50);
            capacity = main.LengthBytes;
        }

        if (needed > capacity)
        {
            throw new BootrixException(ErrorCode.DeviceTooSmall, $"{needed} bytes needed, {capacity} available")
            {
                Arguments = [ByteSize.Format(needed), ByteSize.Format(capacity)],
            };
        }
    }

    /// <summary>Everything written through the file system goes to the device, then the volumes are dismounted so that reading them back cannot be answered from the cache.</summary>
    private void DropCaches(MediaWriteTarget target, StorageDevice device, CancellationToken cancellationToken)
    {
        foreach (var partition in target.Prepared?.Partitions ?? [])
        {
            if (partition.Volume is { } volume)
            {
                VolumeFlusher.Flush(volume.VolumeGuidPath);
            }
        }

        using (var disk = DiskAccess.Open(device, write: true, cancellationToken))
        {
            disk.Flush();
        }

        // Locking dismounts the file systems; Windows mounts them again from the device when the next file is opened.
        using var locks = VolumeLockSet.Acquire(device, Log, cancellationToken);
    }

    private void VerifyRecords(MediaWriteTarget target, StorageDevice device, CancellationToken cancellationToken)
    {
        var plan = target.Plan;
        if (WindowsMbr.IsNeeded(plan))
        {
            TargetBootRecords.VerifyMbr(device, plan, cancellationToken);
        }

        if (NeedsFatBootCode(plan) && _fatBootCode is { } code)
        {
            TargetBootRecords.VerifyBootSectors(device, MainPartition(target), code, cancellationToken);
        }

        foreach (var partition in plan.Partitions.Where(p => p.Role == PartitionRole.UefiNtfs))
        {
            TargetBootRecords.VerifyPartition(device, partition, UefiNtfsFor(plan.SectorSize), cancellationToken);
        }
    }

    private static bool NeedsFatBootCode(MediaPlan plan) =>
        WindowsMbr.IsNeeded(plan)
        && plan.SectorSize == 512
        && plan.Partitions.FirstOrDefault(p => p.Role == PartitionRole.Main)?.FileSystem == FileSystemKind.Fat32;

    private byte[] UefiNtfsFor(int sectorSize)
    {
        lock (_uefiNtfs)
        {
            // Built once: the version for 4096-byte sectors carries a volume serial that is made from the clock.
            if (!_uefiNtfs.TryGetValue(sectorSize, out var image))
            {
                _uefiNtfs[sectorSize] = image = UefiNtfsImage.ForSectorSize(sectorSize);
            }

            return image;
        }
    }

    private static PlannedPartition MainPartition(MediaWriteTarget target) =>
        target.Plan.Partitions.First(p => p.Role == PartitionRole.Main);

    private static VolumeInfo MainVolume(MediaWriteTarget target)
    {
        var index = target.Plan.Partitions.ToList().FindIndex(p => p.Role == PartitionRole.Main);
        return target.Prepared?.Partitions[index].Volume
            ?? throw new BootrixException(ErrorCode.VolumeNotMounted, $"main partition of disk {target.Device.DiskNumber}");
    }

    /// <summary>ERROR_DISK_FULL (112) and ERROR_HANDLE_DISK_FULL (39).</summary>
    private static bool IsDiskFull(IOException ex) => (ex.HResult & 0xFFFF) is 112 or 39;

    private void DeleteWorkDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.LogWarning(ex, "Could not delete the work folder {Path}", path);
        }
    }

    private sealed class TargetRun(WindowsCopyPlan copyPlan)
    {
        public WindowsCopyPlan CopyPlan { get; } = copyPlan;

        public IReadOnlyList<CopiedFile> Copied { get; set; } = [];
    }

    /// <summary>Reports on the thread that produced the value; <see cref="Progress{T}"/> would queue the reports and let them overtake the end of the step.</summary>
    private sealed class InlineProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
