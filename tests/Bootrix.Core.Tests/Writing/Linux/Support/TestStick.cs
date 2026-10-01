// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.FileSystems.Fat;
using Bootrix.Core.Images;
using Bootrix.Core.IO;
using Bootrix.Core.Model;
using Bootrix.Core.Partitioning;
using Bootrix.Core.Planning;
using Bootrix.Core.Profiles;
using Bootrix.Core.Tests.Tooling;
using Bootrix.Core.Writing.Linux;

namespace Bootrix.Core.Tests.Writing.Linux.Support;

/// <summary>
/// A disk image that went through the whole Core side of writing a Linux stick: inspect the ISO, plan the layout,
/// partition and format, copy and patch the files, install the BIOS boot code. Only the platform steps differ on Windows
/// (the volume is mounted there; here the files are put into the FAT image with mtools).
/// </summary>
internal sealed class TestStick : IDisposable
{
    public const long Mib = 1024 * 1024;

    private readonly string _work;

    private TestStick(string work, string imagePath, MediaPlan plan, LinuxBuildResult result, LinuxTreeFacts facts, ImageInspection inspection)
    {
        _work = work;
        ImagePath = imagePath;
        Plan = plan;
        Result = result;
        Facts = facts;
        Inspection = inspection;
    }

    public string ImagePath { get; }

    public MediaPlan Plan { get; }

    public LinuxBuildResult Result { get; }

    public LinuxTreeFacts Facts { get; }

    public ImageInspection Inspection { get; }

    public PlannedPartition Main => Plan.Partitions.Single(p => p.Role == PartitionRole.Main);

    /// <summary>The main partition addressed the way mtools expects it.</summary>
    public string MainSpec => $"{ImagePath}@@{Main.StartBytes}";

    public static TestStick Create(string isoPath, TargetOptions? target = null, long deviceBytes = 256 * Mib, Action<string>? beforeBoot = null)
    {
        var work = Path.Combine(Path.GetTempPath(), "bootrix-stick-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(work);
        var imagePath = Path.Combine(work, "stick.img");

        var inspection = new ImageInspector().InspectAsync(isoPath).GetAwaiter().GetResult();
        var plan = LayoutPlanner.Plan(inspection.Profile, target ?? new TargetOptions { Mode = WriteMode.Extract }, new DeviceCaps { SizeBytes = deviceBytes });

        using var disk = new FileStream(imagePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.RandomAccess);
        disk.SetLength(plan.DeviceBytes);
        DiskLayoutWriter.WriteToStream(disk, plan.ToDiskLayout(mbrSignature: 0x42445258, diskGuid: Guid.Parse("b1c7d2f0-5c1e-4d6a-9d0e-0a3c5a1f7e21")), plan.SectorSize);
        FormatPartitions(disk, plan, inspection.Profile);

        using var iso = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var content = IsoContent.Open(iso);
        var facts = LinuxTreeFacts.Scan(content);
        var settings = LinuxBuildPlanner.Create(plan, inspection.Profile, facts);

        var staging = Path.Combine(work, "files");
        Directory.CreateDirectory(staging);
        var result = new LinuxMediaBuilder().Build(content, new DirectoryVolume(staging), settings);

        var stick = new TestStick(work, imagePath, plan, result, facts, inspection);
        stick.CopyIntoFat(staging, disk);
        LinuxBootCodeInstaller.Install(disk, plan, result.Bios);
        disk.Flush();
        beforeBoot?.Invoke(imagePath);
        return stick;
    }

    /// <summary>Copies the main partition into a file of its own and lets fsck.vfat judge it; the tool cannot address a partition inside an image.</summary>
    public ToolResult FsckMainPartition()
    {
        var partitionFile = Path.Combine(_work, "main.img");
        using (var disk = new FileStream(ImagePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var target = new FileStream(partitionFile, FileMode.Create, FileAccess.Write))
        {
            disk.Position = Main.StartBytes;
            var remaining = Main.LengthBytes;
            var buffer = new byte[1024 * 1024];
            while (remaining > 0)
            {
                var read = disk.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                target.Write(buffer, 0, read);
                remaining -= read;
            }
        }

        return ExternalTools.Run("fsck.vfat", "-n", partitionFile);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (IOException)
        {
            // A temporary folder that stays behind is not worth a failed test.
        }
    }

    private static void FormatPartitions(FileStream disk, MediaPlan plan, ImageProfile profile)
    {
        var traits = Bootrix.Core.Images.Policy.ImagePolicy.Default.TraitsOf(profile.Family);
        foreach (var partition in plan.Partitions)
        {
            using var slice = new StreamSlice(disk, partition.StartBytes, partition.LengthBytes);
            switch (partition.FileSystem)
            {
                case FileSystemKind.Fat12 or FileSystemKind.Fat16 or FileSystemKind.Fat32:
                    FatFormatter.Format(slice, plan.ToFatOptions(partition) with { AssumeZeroed = true, VolumeId = 0x0BADF00D });
                    break;
                case FileSystemKind.Ext3:
                    PersistenceStore.Format(slice, partition, traits);
                    break;
            }
        }
    }

    /// <summary>ldlinux.sys first, so that it lies in one piece at the start of the empty volume; then everything else.</summary>
    private void CopyIntoFat(string staging, FileStream disk)
    {
        disk.Flush();
        var first = Path.Combine(staging, "ldlinux.sys");
        if (File.Exists(first))
        {
            Mcopy(first, "::ldlinux.sys");
            File.Delete(first);
        }

        foreach (var entry in Directory.EnumerateFileSystemEntries(staging))
        {
            Mcopy(entry, "::", recursive: Directory.Exists(entry));
        }
    }

    private void Mcopy(string source, string target, bool recursive = false)
    {
        var arguments = new List<string> { "-i", MainSpec, "-o", "-Q" };
        if (recursive)
        {
            arguments.Add("-s");
        }

        arguments.Add(source);
        arguments.Add(target);
        var result = ExternalTools.Run("mcopy", [.. arguments]);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException("mcopy failed: " + result.Combined);
        }
    }
}
