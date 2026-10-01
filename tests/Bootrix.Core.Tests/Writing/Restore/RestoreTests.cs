// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.FileSystems.Fat;
using Bootrix.Core.Model;
using Bootrix.Core.Partitioning;
using Bootrix.Core.Planning;
using Bootrix.Core.Storage;
using Bootrix.Core.Tests.Images.Support;
using Bootrix.Core.Tests.Tooling;
using Bootrix.Core.Tests.Writing.Raw;
using Bootrix.Core.Writing.Restore;

namespace Bootrix.Core.Tests.Writing.Restore;

/// <summary>
/// Restoring a stick: the wipe against disks that carry a hybrid image or a GPT, and the plan carried out on a disk file
/// the way the Windows job does it, checked with sfdisk, sgdisk and fsck.vfat.
/// </summary>
public sealed class RestoreTests : IDisposable
{
    private const long Mib = 1024 * 1024;

    private readonly TestDirectory _dir = new("bootrix-restore");

    public void Dispose() => _dir.Dispose();

    private FileBlockDevice Stick(long size, byte fill, byte[]? image = null)
    {
        var path = _dir.File($"stick-{Guid.NewGuid():N}.img");
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            file.SetLength(size);
            var block = Enumerable.Repeat(fill, (int)Mib).ToArray();
            for (var offset = 0L; fill != 0 && offset < size; offset += Mib)
            {
                file.Position = offset;
                file.Write(block);
            }

            if (image is not null)
            {
                file.Position = 0;
                file.Write(image);
            }
        }

        return new FileBlockDevice(path, size, create: false);
    }

    private static bool IsZero(FileBlockDevice device, long offset, long length)
    {
        var buffer = new byte[length];
        device.Read(offset, buffer);
        return buffer.All(b => b == 0);
    }

    private byte[] GptImage(long size) => File.ReadAllBytes(DiskImageBuilder.CreateGpt(
        _dir, "gpt-" + Guid.NewGuid().ToString("N") + ".img", size, new GptPartitionSpec(2048, 8192, "0700", "DATA")));

    [ToolFact("sgdisk")]
    public void Wipe_ClearsTheBackupGptBehindTheOldImage_AndLeavesTheRest()
    {
        var image = GptImage(24 * Mib);
        using var device = Stick(64 * Mib, fill: 0xEE, image);
        var backupStart = (24 * Mib) - (33 * 512);

        var extra = DriveWiper.Wipe(device);

        Assert.Equal([new ByteRange(backupStart, 33 * 512)], extra);
        Assert.True(IsZero(device, 0, 8 * Mib));
        Assert.True(IsZero(device, backupStart, 33 * 512));
        Assert.True(IsZero(device, 63 * Mib, Mib));
        Assert.False(IsZero(device, 30 * Mib, 4096));
        Assert.False(IsZero(device, 40 * Mib, 4096));
        var after = ReferenceTool.RunUnchecked("sgdisk", ["-p", device.Name]);
        Assert.DoesNotContain("GPT: present", after.StandardOutput, StringComparison.Ordinal);
    }

    [RequiresToolFact("sgdisk")]
    public void Wipe_FindsTheBackupEvenWhenTheHeaderChecksumIsBroken()
    {
        var image = GptImage(24 * Mib);
        image[512 + 40] ^= 0x01;
        using var device = Stick(64 * Mib, fill: 0, image);
        device.Write(24 * Mib - 512, Enumerable.Repeat((byte)0x77, 512).ToArray());

        var extra = DriveWiper.Wipe(device);

        Assert.Single(extra);
        Assert.True(IsZero(device, 24 * Mib - 512, 512));
    }

    [RequiresToolFact("sgdisk")]
    public void Wipe_OfASmallImage_NeedsNothingBeyondTheStartAndTheEnd()
    {
        var image = GptImage(6 * Mib);
        using var device = Stick(64 * Mib, fill: 0xEE, image);

        var extra = DriveWiper.Wipe(device);

        Assert.Empty(extra);
        Assert.True(IsZero(device, 0, 8 * Mib));
    }

    [Fact]
    public void Wipe_OfADiskWithoutGpt_TouchesOnlyTheEnds()
    {
        using var device = Stick(32 * Mib, fill: 0xEE);

        var extra = DriveWiper.Wipe(device);

        Assert.Empty(extra);
        Assert.True(IsZero(device, 0, 8 * Mib));
        Assert.True(IsZero(device, 31 * Mib, Mib));
        Assert.False(IsZero(device, 16 * Mib, 4096));
    }

    [Fact]
    public void Wipe_IgnoresAHeaderThatPointsOutsideTheDisk()
    {
        var image = new byte[64 * 1024];
        "EFI PART"u8.CopyTo(image.AsSpan(512));
        BitConverter.TryWriteBytes(image.AsSpan(512 + 32), long.MaxValue);
        BitConverter.TryWriteBytes(image.AsSpan(512 + 80), 128u);
        BitConverter.TryWriteBytes(image.AsSpan(512 + 84), 128u);
        using var device = Stick(32 * Mib, fill: 0, image);

        Assert.Empty(DriveWiper.Wipe(device));
    }

    public static TheoryData<string, long> Layouts() => new()
    {
        { "Mbr", 64 * Mib },
        { "Gpt", 64 * Mib },
        { "Mbr", 200 * Mib },
    };

    [ToolTheory("sfdisk", "sgdisk", "fsck.vfat", "mlabel")]
    [MemberData(nameof(Layouts))]
    public void Fat32Restore_LeavesOneCleanPartitionOverTheWholeDisk(string scheme, long size)
    {
        var image = GptImage(24 * Mib);
        using var device = Stick(size, fill: 0xEE, image);
        var options = new RestoreOptions { Scheme = Enum.Parse<PartitionScheme>(scheme), FileSystem = FileSystemKind.Fat32, Label = "DATEN" };
        var plan = RestorePlanner.Plan(options, new DeviceCaps { SizeBytes = size });

        Apply(device, plan);

        var (label, partitions) = HybridImages.Table(device.Name);
        Assert.Equal(scheme == "Gpt" ? "gpt" : "dos", label);
        var partition = Assert.Single(partitions);
        Assert.False(partition.Bootable);
        Assert.Equal(PlanLayout(plan), (partition.Start * 512, partition.Size * 512));
        Assert.True(partition.Size * 512 >= size - (3 * Mib));
        var fs = HybridImages.Extract(_dir, device.Name, partition.Start * 512, partition.Size * 512, "fat-" + Guid.NewGuid().ToString("N"));
        var fsck = ReferenceTool.RunUnchecked("fsck.vfat", ["-n", fs]);
        Assert.True(fsck.ExitCode == 0, fsck.StandardOutput + fsck.StandardError);
        Assert.Contains("DATEN", Tooling.ExternalTools.Run("mlabel", "-i", fs, "-s", "::").Output, StringComparison.Ordinal);
        if (scheme == "Gpt")
        {
            Assert.Contains("No problems found", ReferenceTool.Run("sgdisk", ["--verify", device.Name]).StandardOutput, StringComparison.Ordinal);
        }
    }

    [ToolFact("sfdisk")]
    public void Restore_RemovesTheOldImageTables_SoNoOldPartitionComesBack()
    {
        var image = File.ReadAllBytes(HybridImages.BuildIso(_dir, HybridStyle.DebianMbr));
        using var device = Stick(64 * Mib, fill: 0, image);
        var plan = RestorePlanner.Plan(new RestoreOptions { Scheme = PartitionScheme.Mbr, FileSystem = FileSystemKind.Fat32 }, new DeviceCaps { SizeBytes = 64 * Mib });

        Apply(device, plan);

        Assert.Single(HybridImages.Table(device.Name).Partitions);

        // The volume descriptor of the old ISO 9660 file system (sector 16) is gone as well.
        Assert.True(IsZero(device, 32768, 2048));
    }

    [Theory]
    [InlineData(FileSystemKind.Ntfs)]
    [InlineData(FileSystemKind.ExFat)]
    [InlineData(FileSystemKind.Fat32)]
    public void Plan_HasOnePartitionOverTheDisk_InTheChosenFileSystem(FileSystemKind fileSystem)
    {
        var plan = RestorePlanner.Plan(
            new RestoreOptions { Scheme = PartitionScheme.Gpt, FileSystem = fileSystem, Label = "BACKUP" },
            new DeviceCaps { SizeBytes = 16L * 1024 * Mib });

        var partition = Assert.Single(plan.Partitions);
        Assert.Equal(PartitionRole.Main, partition.Role);
        Assert.Equal(fileSystem, partition.FileSystem);
        Assert.Equal("BACKUP", partition.Label);
        Assert.False(partition.Active);
        Assert.Equal(PartitionScheme.Gpt, plan.Scheme);
        Assert.Equal(WriteMethod.FormatOnly, plan.WriteMethod);
        Assert.Equal(GptTypes.BasicData, partition.GptType);
    }

    [Fact]
    public void Plan_WithAutomaticChoices_UsesFat32ForSmallAndExFatForLargeDrives()
    {
        var small = RestorePlanner.Plan(new RestoreOptions(), new DeviceCaps { SizeBytes = 8L * 1024 * Mib });
        var large = RestorePlanner.Plan(new RestoreOptions(), new DeviceCaps { SizeBytes = 256L * 1024 * Mib });

        Assert.Equal(FileSystemKind.Fat32, small.Partitions[0].FileSystem);
        Assert.Equal(FileSystemKind.ExFat, large.Partitions[0].FileSystem);
        Assert.Equal(PartitionScheme.Mbr, small.Scheme);
    }

    [Fact]
    public void Plan_ForATinyDrive_IsRefusedByTheFileSystemRules()
    {
        var error = Assert.Throws<BootrixException>(() => RestorePlanner.Plan(
            new RestoreOptions { FileSystem = FileSystemKind.Fat32 },
            new DeviceCaps { SizeBytes = 2 * Mib }));

        Assert.True(error.Code is ErrorCode.FileSystemTooSmall or ErrorCode.DeviceTooSmall);
    }

    private static (long Start, long Length) PlanLayout(MediaPlan plan) => (plan.Partitions[0].StartBytes, plan.Partitions[0].LengthBytes);

    /// <summary>What the Windows job does with its disk handle: wipe, format the FAT partition in place, write the table.</summary>
    private static void Apply(FileBlockDevice device, MediaPlan plan)
    {
        DriveWiper.Wipe(device);
        using var disk = new BlockDeviceStream(device, 0, device.Length);
        foreach (var partition in plan.Partitions.Where(p => p.FileSystem == FileSystemKind.Fat32))
        {
            using var stream = new BlockDeviceStream(device, partition.StartBytes, partition.LengthBytes);
            FatFormatter.Format(stream, plan.ToFatOptions(partition));
            stream.Flush();
        }

        DiskLayoutWriter.WriteToStream(disk, plan.ToDiskLayout(mbrSignature: 0x1234ABCD), 512);
        disk.Flush();
    }
}
