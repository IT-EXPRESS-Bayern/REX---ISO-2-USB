// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Bootrix.Core.Images;
using Bootrix.Core.Jobs;
using Bootrix.Core.Planning;
using Bootrix.Core.Profiles;
using Bootrix.Core.Storage;
using Bootrix.Core.Writing.Windows;
using Bootrix.Windows.Storage;
using Bootrix.Windows.Writing;
using Bootrix.Windows.Writing.Windows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Windows.Tests.Writing.Windows;

/// <summary>A fact that needs xorriso to build the ISO it writes from.</summary>
public sealed class RequiresXorrisoFactAttribute : FactAttribute
{
    public RequiresXorrisoFactAttribute()
    {
        if (!WindowsWriteKit.HasTool("xorriso"))
        {
            Skip = "xorriso is not installed";
        }
    }
}

/// <summary>
/// Everything a test of the Windows setup writer needs: ISO files, folders that stand in for the volumes of
/// the targets, plans for small sticks, and a recording stand-in for the disk operations.
/// </summary>
internal sealed class WindowsWriteKit : IDisposable
{
    public const long Mib = 1024 * 1024;

    public WindowsWriteKit()
    {
        Directory = Path.Combine(Path.GetTempPath(), "bootrix-windows-write-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
    }

    public string Directory { get; }

    public static bool HasTool(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Any(dir => File.Exists(Path.Combine(dir, name)));

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is not worth failing a test for.
        }
    }

    public static byte[] Random(int length, int seed)
    {
        var data = new byte[length];
        new System.Random(seed).NextBytes(data);
        return data;
    }

    public static Dictionary<string, byte[]> SetupFiles() => new()
    {
        ["bootmgr"] = Random(40_000, 1),
        ["bootmgr.efi"] = Random(30_000, 2),
        ["boot/bcd"] = Random(16_384, 3),
        ["boot/boot.sdi"] = Random(70_000, 4),
        ["efi/boot/bootx64.efi"] = Random(50_000, 5),
        ["efi/microsoft/boot/bcd"] = Random(16_384, 6),
        ["sources/boot.wim"] = Random(300_000, 7),
        ["sources/install.wim"] = Random(2_000_000, 8),
        ["sources/setup.exe"] = Random(25_000, 9),
        ["setup.exe"] = Random(20_000, 10),
        ["autorun.inf"] = Random(60, 11),
        ["support/empty.txt"] = [],
    };

    /// <summary>Builds an ISO with xorriso; returns its path and the inspection the job factory would have made.</summary>
    public async Task<(string Path, ImageInspection Inspection)> BuildIsoAsync(Dictionary<string, byte[]> files, string name = "setup")
    {
        var tree = System.IO.Path.Combine(Directory, "tree-" + name);
        foreach (var (relative, content) in files)
        {
            var path = System.IO.Path.Combine(tree, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
        }

        var iso = System.IO.Path.Combine(Directory, name + ".iso");
        var start = new ProcessStartInfo("xorriso", ["-as", "mkisofs", "-quiet", "-r", "-J", "-iso-level", "3", "-V", "WINTEST", "-o", iso, tree])
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        using (var process = Process.Start(start)!)
        {
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, await process.StandardError.ReadToEndAsync());
        }

        return (iso, await new ImageInspector().InspectAsync(iso));
    }

    public static ImageProfile Image(WindowsArch arch = WindowsArch.X64) => new()
    {
        Kind = ImageKind.WindowsSetup,
        VolumeLabel = "WINTEST",
        TotalBytes = 20 * Mib,
        LargestFileBytes = 10 * Mib,
        HasBiosBootFiles = true,
        HasEfiBootFiles = true,
        Arch = arch,
        WindowsBuild = 26200,
    };

    public static MediaPlan Plan(TargetOptions? options = null, long deviceBytes = 256 * Mib, int sectorSize = 512, ImageProfile? image = null) =>
        LayoutPlanner.Plan(
            image ?? Image(),
            options ?? new TargetOptions(),
            new DeviceCaps
            {
                SizeBytes = deviceBytes,
                LogicalSectorSize = sectorSize,
                PhysicalSectorSize = sectorSize,
                Bus = DeviceBus.Usb,
                Medium = DeviceMedium.Stick,
                Removable = true,
            });

    public static StorageDevice Stick(int diskNumber = 3, long bytes = 256 * Mib) => new()
    {
        DiskNumber = diskNumber,
        DevicePath = $@"\\?\usbstor#disk&ven_test#{diskNumber}",
        Serial = "S" + diskNumber,
        SizeBytes = bytes,
        Bus = BusType.Usb,
        IsRemovableMedia = true,
    };

    /// <summary>A target whose partitions are folders: <see cref="MediaWriteTarget.Prepared"/> is filled in the way the preparer would fill it.</summary>
    public MediaWriteTarget Target(MediaPlan plan, int diskNumber = 3)
    {
        var device = Stick(diskNumber, plan.DeviceBytes);
        var target = new MediaWriteTarget { Device = device, Identity = DiskIdentity.From(device), Plan = plan };
        var partitions = new List<PreparedPartition>();
        for (var index = 0; index < plan.Partitions.Count; index++)
        {
            VolumeInfo? volume = null;
            if (plan.Partitions[index].FileSystem is not null)
            {
                var folder = System.IO.Path.Combine(Directory, $"volume-{diskNumber}-{index}");
                System.IO.Directory.CreateDirectory(folder);
                volume = new VolumeInfo { VolumeGuidPath = folder + System.IO.Path.DirectorySeparatorChar };
            }

            partitions.Add(new PreparedPartition(index, plan.Partitions[index].StartBytes, volume));
        }

        target.Prepared = new PreparedDisk(partitions);
        return target;
    }

    public string MainRoot(MediaWriteTarget target) =>
        target.Prepared!.Partitions[target.Plan.Partitions.ToList().FindIndex(p => p.Role == PartitionRole.Main)].Volume!.VolumeGuidPath;

    public MediaWriteContext Context(string imagePath, ImageInspection inspection, IReadOnlyList<MediaWriteTarget> targets, JobSpec? spec = null) => new()
    {
        JobId = "write-test",
        ImagePath = imagePath,
        Inspection = inspection,
        Spec = spec ?? new JobSpec(),
        Targets = targets,
        WorkDirectory = System.IO.Path.Combine(Directory, "work"),
    };

    public static WriteServices Services(ILoggerFactory? loggers = null) =>
        new(new NoDisks(), new DiskPreparer(), new JobJournal(System.IO.Path.GetTempPath()), loggers ?? NullLoggerFactory.Instance);

    private sealed class NoDisks : IDiskService
    {
        public event EventHandler? DevicesChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyList<StorageDevice> Enumerate(DiskFilter filter) => [];

        public StorageDevice? Find(string devicePath) => null;
    }

    /// <summary>Runs steps through the real job runner and collects what it reports.</summary>
    public static async Task<(JobResult Result, List<ProgressReport> Reports)> RunAsync(IEnumerable<IJobStep> steps, CancellationToken cancellationToken = default)
    {
        var reports = new List<ProgressReport>();
        var result = await new JobRunner(NullLogger<JobRunner>.Instance).RunAsync(
            new Job("test", "test", [.. steps]),
            new DelegateProgressSink(reports.Add),
            cancellationToken);
        return (result, reports);
    }
}

/// <summary>Records what the steps ask of the disk and can be told to interfere, as a damaged stick would.</summary>
internal sealed class FakeTargetOps : ITargetOps
{
    public List<string> Calls { get; } = [];

    public List<string> FlushedVolumes { get; } = [];

    public List<(PlannedPartition Partition, byte[] Expected)> VerifiedPartitions { get; } = [];

    public FatBootSectors? VerifiedBootSectors { get; private set; }

    /// <summary>Called when the caches are dropped, i.e. between the end of the copy and the read-back.</summary>
    public Action<MediaWriteTarget>? OnDropCaches { get; set; }

    public StorageDevice Current(MediaWriteTarget target) => target.Device;

    public void FlushVolume(string volumeGuidPath)
    {
        Calls.Add("flush");
        FlushedVolumes.Add(volumeGuidPath);
    }

    public void DropCaches(MediaWriteTarget target, StorageDevice device, CancellationToken cancellationToken)
    {
        Calls.Add("drop-caches");
        OnDropCaches?.Invoke(target);
    }

    public bool WriteMbr(StorageDevice device, MediaPlan plan, CancellationToken cancellationToken)
    {
        Calls.Add("write-mbr");
        return false;
    }

    public void VerifyMbr(StorageDevice device, MediaPlan plan, CancellationToken cancellationToken) => Calls.Add("verify-mbr");

    public void VerifyBootSectors(StorageDevice device, PlannedPartition main, FatBootSectors expected, CancellationToken cancellationToken)
    {
        Calls.Add("verify-boot-sectors");
        VerifiedBootSectors = expected;
    }

    public void VerifyPartition(StorageDevice device, PlannedPartition partition, byte[] expected, CancellationToken cancellationToken)
    {
        Calls.Add("verify-partition");
        VerifiedPartitions.Add((partition, expected));
    }
}

/// <summary>Hands out a fixed set of boot sectors and counts the requests.</summary>
internal sealed class FakeBootCode(FatBootSectors? sectors = null) : IVbrCodeSource
{
    public int Requests { get; private set; }

    public Task<FatBootSectors> ReadFat32Async(string scratchDirectory, CancellationToken cancellationToken)
    {
        Requests++;
        return Task.FromResult(sectors ?? new FatBootSectors(new byte[13 * 512]));
    }
}

internal sealed class FakeCustomizer(string id, bool applies = true) : IWindowsMediaCustomizer
{
    public List<WindowsMediaCustomization> Applied { get; } = [];

    /// <summary>Whether the work folder existed while the customizer ran; it is deleted when the job ends.</summary>
    public List<bool> WorkFolderExisted { get; } = [];

    public string Id => id;

    public bool Applies(MediaWriteContext write) => applies;

    public Task ApplyAsync(WindowsMediaCustomization customization, IProgress<double> progress, CancellationToken cancellationToken)
    {
        Applied.Add(customization);
        WorkFolderExisted.Add(System.IO.Directory.Exists(customization.WorkDirectory));
        progress.Report(0.5);
        progress.Report(1);
        return Task.CompletedTask;
    }
}
