// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Boot.Dos;
using Bootrix.Core.Errors;
using Bootrix.Core.FileSystems.Fat;
using Bootrix.Core.Model;
using Bootrix.Core.Planning;
using Bootrix.Core.Tests.FileSystems.Fat;
using Bootrix.Core.Tests.Tooling;
using Bootrix.Core.Writing.Dos;
using DiscFatFileSystem = DiscUtils.Fat.FatFileSystem;

namespace Bootrix.Core.Tests.Writing.Dos;

public class DosVolumeTests
{
    private const long Mib = DosImageBuilder.Mib;

    /// <summary>Copies the partition out of the disk image; fsck and mtools read a plain volume.</summary>
    private static TempImage ExtractMain(TempImage disk, MediaPlan plan)
    {
        var main = plan.Partitions.Single(p => p.Role == PartitionRole.Main);
        var volume = new TempImage(main.LengthBytes);
        using var source = disk.Open();
        using var target = volume.Open();
        source.Position = main.StartBytes;
        var buffer = new byte[1 << 20];
        for (long left = main.LengthBytes; left > 0;)
        {
            var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
            target.Write(buffer, 0, read);
            left -= read;
        }

        return volume;
    }

    [RequiresToolTheory("fsck.vfat", "mdir", "mattrib")]
    [InlineData(8, FileSystemKind.Fat12, false)]
    [InlineData(64, FileSystemKind.Fat16, false)]
    [InlineData(64, FileSystemKind.Fat16, true)]
    [InlineData(300, FileSystemKind.Fat32, false)]
    [InlineData(300, FileSystemKind.Fat32, true)]
    public void StickVolume_IsCleanAccordingToFsck_AndHoldsTheFilesWithTheirAttributes(int megabytes, FileSystemKind fileSystem, bool legacy)
    {
        var plan = DosImageBuilder.PlanStick(megabytes * Mib, fileSystem, legacy);
        using var disk = DosImageBuilder.BuildStick(plan, FreeDosSystem.Create());
        using var volume = ExtractMain(disk, plan);

        _ = FatVerifier.Fsck(volume.Path);

        var listing = ExternalTools.Run("mdir", "-a", "-i", volume.Path, "::").Output;
        foreach (var name in new[] { "KERNEL", "COMMAND", "FDCONFIG", "AUTOEXEC" })
        {
            Assert.Contains(name, listing, StringComparison.Ordinal);
        }

        var attributes = ExternalTools.Run("mattrib", "-i", volume.Path, "::KERNEL.SYS", "::COMMAND.COM", "::AUTOEXEC.BAT").Output;
        Assert.Matches(@"A\s+SHR\s+::/KERNEL\.SYS", attributes);
        Assert.Matches(@"A\s+SHR\s+::/COMMAND\.COM", attributes);
        Assert.Matches(@"A\s+::/AUTOEXEC\.BAT", attributes);
    }

    [Fact]
    public void Files_AreReadBackByteForByte_ByAnIndependentReader()
    {
        var plan = DosImageBuilder.PlanStick(64 * Mib, FileSystemKind.Fat16);
        var system = FreeDosSystem.Create().WithFile(new DosFile("\\SUB\\DATA.BIN", [1, 2, 3, 4, 5]));
        using var disk = DosImageBuilder.BuildStick(plan, system);
        var main = plan.Partitions.Single();

        using var stream = disk.Open();
        using var slice = new Bootrix.Core.IO.StreamSlice(stream, main.StartBytes, main.LengthBytes);
        using var fat = new DiscFatFileSystem(slice);
        foreach (var file in system.Files)
        {
            using var read = fat.OpenFile(file.Path, FileMode.Open, FileAccess.Read);
            var content = new byte[read.Length];
            read.ReadExactly(content);
            Assert.Equal(file.Content, content);
            Assert.Equal(file.Attributes, fat.GetAttributes(file.Path) & ~FileAttributes.Directory);
        }
    }

    [Fact]
    public void SystemFiles_OccupyConsecutiveClusters_InTheOrderTheyWereWritten()
    {
        var plan = DosImageBuilder.PlanFloppy();
        using var disk = new TempImage(plan.DeviceBytes);
        var system = FreeDosSystem.Create(floppy: true);
        using var stream = disk.Open();
        FatFormatter.Format(stream, system.Customize(plan.ToFatOptions(plan.Partitions[0]), plan.TotalSectors));
        DosVolumeWriter.Write(stream, system.Files);

        // The directory entries hold the first cluster; consecutive files start where the previous one ended.
        var root = new byte[224 * 32];
        stream.Position = (1 + 2 * 9) * 512;
        stream.ReadExactly(root);
        var firstClusters = new Dictionary<string, int>();
        for (var i = 0; i < 224 && root[i * 32] != 0; i++)
        {
            var entry = root.AsSpan(i * 32, 32);
            if (entry[0] != 0xE5 && entry[11] != 0x0F && (entry[11] & 0x08) == 0)
            {
                firstClusters[System.Text.Encoding.ASCII.GetString(entry[..11])] = BitConverter.ToUInt16(entry[26..28]);
            }
        }

        var kernelClusters = (system.Files[0].Content.Length + 511) / 512;
        Assert.Equal(2, firstClusters["KERNEL  SYS"]);
        Assert.Equal(2 + kernelClusters, firstClusters["COMMAND COM"]);
    }

    [Fact]
    public void Write_RefusesFilesThatDoNotFit()
    {
        var plan = DosImageBuilder.PlanFloppy(368_640);
        using var disk = new TempImage(plan.DeviceBytes);
        using var stream = disk.Open();
        FatFormatter.Format(stream, plan.ToFatOptions(plan.Partitions[0]));

        var ex = Assert.Throws<BootrixException>(() =>
            DosVolumeWriter.Write(stream, [new DosFile("\\BIG.BIN", new byte[400_000])]));

        Assert.Equal(ErrorCode.DeviceTooSmall, ex.Code);
    }

    [Fact]
    public void Write_CreatesTheFoldersOfNestedFilesOnce()
    {
        var plan = DosImageBuilder.PlanStick(64 * Mib, FileSystemKind.Fat16);
        using var disk = new TempImage(plan.DeviceBytes);
        using var stream = disk.Open();
        FatFormatter.Format(stream, plan.ToFatOptions(plan.Partitions[0]) with { HiddenSectors = 0 });

        DosVolumeWriter.Write(stream, [new DosFile("\\LOCALE\\A.TXT", [1]), new DosFile("\\LOCALE\\B.TXT", [2])]);

        using var fat = new DiscFatFileSystem(stream);
        Assert.Equal(["A.TXT", "B.TXT"], fat.GetFiles("\\LOCALE").Select(Path.GetFileName).Order());
        Assert.Equal(["LOCALE"], fat.GetDirectories("\\").Select(Path.GetFileName));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MbrAndBpb_DescribeTheSameGeometry_SoCylinderHeadSectorValuesAgree(bool legacy)
    {
        var plan = DosImageBuilder.PlanStick(64 * Mib, FileSystemKind.Fat16, legacy);
        using var disk = DosImageBuilder.BuildStick(plan, FreeDosSystem.Create());
        var main = plan.Partitions.Single();

        using var stream = disk.Open();
        var sector = new byte[512];
        stream.ReadExactly(sector);
        var entry = Bootrix.Core.Partitioning.Mbr.Parse(sector).Entries[0];
        stream.Position = main.StartBytes;
        stream.ReadExactly(sector);
        var geometry = new Bootrix.Core.Partitioning.ChsGeometry(BitConverter.ToUInt16(sector, 0x1A), BitConverter.ToUInt16(sector, 0x18));

        Assert.Equal(main.StartLba(512), entry.StartLba);
        Assert.Equal(entry.StartLba, BitConverter.ToUInt32(sector, 0x1C));
        Assert.Equal(geometry.FromLba(entry.StartLba), entry.FirstChs);
        Assert.Equal(legacy ? 128u : 2048u, entry.StartLba);
    }
}
