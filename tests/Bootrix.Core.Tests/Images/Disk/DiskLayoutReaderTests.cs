// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.Json;
using Bootrix.Core.Images.Disk;
using Bootrix.Core.Tests.Images.Support;

namespace Bootrix.Core.Tests.Images.Disk;

public sealed class DiskLayoutReaderTests : IDisposable
{
    private const long MiB = 1024 * 1024;

    private readonly TestDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private static DiskLayout Read(string path)
    {
        using var stream = File.OpenRead(path);
        return DiskLayoutReader.Read(stream);
    }

    /// <summary><c>sfdisk --json</c> as an independent description of the partition table.</summary>
    private static JsonElement SfdiskTable(string path)
    {
        var json = ExternalTool.Run("sfdisk", "--json", path).StandardOutput;
        return JsonDocument.Parse(json).RootElement.GetProperty("partitiontable");
    }

    [ToolFact("sfdisk")]
    public void Read_MbrDisk_MatchesSfdisk()
    {
        var path = DiskImageBuilder.CreateMbr(_dir, "mbr.img", 64 * MiB,
            new MbrPartitionSpec(2048, 20_000, "c", Bootable: true),
            new MbrPartitionSpec(30_000, 40_000, "83"),
            new MbrPartitionSpec(80_000, 8_192, "ef"));
        DiskImageBuilder.WriteAt(path, 0, [0xFA, 0x31, 0xC0, 0x8E, 0xD8]);

        var layout = Read(path);
        var reference = SfdiskTable(path).GetProperty("partitions").EnumerateArray().ToList();

        Assert.True(layout.HasMbrSignature);
        Assert.True(layout.HasBootCode);
        Assert.False(layout.HasGpt);
        Assert.Equal(reference.Count, layout.MbrPartitions.Count);
        for (var i = 0; i < reference.Count; i++)
        {
            Assert.Equal(reference[i].GetProperty("start").GetInt64(), layout.MbrPartitions[i].StartSector);
            Assert.Equal(reference[i].GetProperty("size").GetInt64(), layout.MbrPartitions[i].SectorCount);
            Assert.Equal(Convert.ToInt32(reference[i].GetProperty("type").GetString(), 16), layout.MbrPartitions[i].Type);
            Assert.Equal(reference[i].TryGetProperty("bootable", out var bootable) && bootable.GetBoolean(), layout.MbrPartitions[i].Active);
        }

        Assert.True(layout.HasEfiSystemPartition);
        Assert.True(layout.IsBootableDisk);
    }

    [ToolFact("sfdisk")]
    public void Read_MbrDiskWithoutBootCodeOrEsp_IsNotBootable()
    {
        var path = DiskImageBuilder.CreateMbr(_dir, "data.img", 32 * MiB, new MbrPartitionSpec(2048, 20_000, "83"));

        var layout = Read(path);

        Assert.False(layout.HasBootCode);
        Assert.False(layout.IsBootableDisk);
    }

    [ToolFact("sgdisk", "sfdisk")]
    public void Read_GptDisk_MatchesSfdiskAndSgdisk()
    {
        var path = DiskImageBuilder.CreateGpt(_dir, "gpt.img", 64 * MiB,
            new GptPartitionSpec(2048, 8192, "ef00", "EFI-SYSTEM"),
            new GptPartitionSpec(16384, 16384, "8300", "ROOT-A"),
            new GptPartitionSpec(40000, 1000, "a501", "freebsd boot"));

        var layout = Read(path);
        var table = SfdiskTable(path);
        var reference = table.GetProperty("partitions").EnumerateArray().ToList();
        var verify = ExternalTool.Run("sgdisk", "-v", path).StandardOutput;

        Assert.True(layout.HasGpt);
        Assert.True(layout.GptHeaderValid);
        Assert.Contains("No problems found", verify, StringComparison.Ordinal);
        Assert.Equal(512, layout.GptSectorSize);
        Assert.Equal(64 * MiB / 512 - 1, layout.GptBackupSector);
        Assert.Equal(Guid.Parse(table.GetProperty("id").GetString()!), layout.GptDiskId);
        Assert.True(layout.HasProtectiveMbr);
        Assert.Equal(reference.Count, layout.GptPartitions.Count);
        for (var i = 0; i < reference.Count; i++)
        {
            Assert.Equal(Guid.Parse(reference[i].GetProperty("type").GetString()!), layout.GptPartitions[i].TypeId);
            Assert.Equal(Guid.Parse(reference[i].GetProperty("uuid").GetString()!), layout.GptPartitions[i].UniqueId);
            Assert.Equal(reference[i].GetProperty("start").GetInt64(), layout.GptPartitions[i].FirstSector);
            Assert.Equal(reference[i].GetProperty("start").GetInt64() + reference[i].GetProperty("size").GetInt64() - 1, layout.GptPartitions[i].LastSector);
            Assert.Equal(reference[i].GetProperty("name").GetString(), layout.GptPartitions[i].Name);
        }

        Assert.True(layout.GptPartitions[0].IsEfiSystem);
        Assert.Equal(GptTypes.FreeBsdBoot, layout.GptPartitions[2].TypeId);
        Assert.True(layout.HasEfiSystemPartition);
    }

    [ToolTheory("sgdisk")]
    [InlineData("ef00", "EfiSystem")]
    [InlineData("ef02", "BiosBoot")]
    [InlineData("0700", "MicrosoftBasicData")]
    [InlineData("8300", "LinuxFilesystem")]
    [InlineData("af00", "AppleHfsPlus")]
    [InlineData("af0a", "AppleApfs")]
    [InlineData("a501", "FreeBsdBoot")]
    [InlineData("a503", "FreeBsdUfs")]
    [InlineData("a504", "FreeBsdZfs")]
    [InlineData("a502", "FreeBsdSwap")]
    [InlineData("a600", "OpenBsdData")]
    [InlineData("a902", "NetBsdFfs")]
    public void GptTypes_MatchTheCodesOfSgdisk(string code, string constant)
    {
        var path = DiskImageBuilder.CreateGpt(_dir, "type-" + code + ".img", 8 * MiB, new GptPartitionSpec(2048, 2048, code, "x"));

        var type = Read(path).GptPartitions.Single().TypeId;

        Assert.Equal((Guid)typeof(GptTypes).GetField(constant)!.GetValue(null)!, type);
    }

    [ToolFact("sgdisk")]
    public void Read_GptWithDamagedHeader_IsReportedAsInvalid()
    {
        var path = DiskImageBuilder.CreateGpt(_dir, "bad.img", 16 * MiB, new GptPartitionSpec(2048, 2048, "ef00", "ESP"));
        // First usable LBA (offset 40 of the header) from 34 to 35.
        DiskImageBuilder.WriteAt(path, 512 + 40, [35]);

        var layout = Read(path);

        Assert.True(layout.HasGpt);
        Assert.False(layout.GptHeaderValid);
    }

    [ToolFact("sgdisk", "losetup")]
    public void Read_4KnGpt_ReportsTheSectorSize()
    {
        var path = DiskImageBuilder.Create(_dir, "4kn.img", 32 * MiB);
        var device = ExternalTool.RunUnchecked("losetup", ["--find", "--show", "--sector-size", "4096", path]);
        if (device.ExitCode != 0)
        {
            // Loop devices are not available (unprivileged runner); nothing to compare against.
            return;
        }

        var loop = device.StandardOutput.Trim();
        try
        {
            ExternalTool.Run("sgdisk", "-n", "1:256:+1024", "-t", "1:ef00", "-c", "1:Four K", loop);
        }
        finally
        {
            ExternalTool.RunUnchecked("losetup", ["-d", loop]);
        }

        var layout = Read(path);

        Assert.True(layout.HasGpt);
        Assert.Equal(4096, layout.GptSectorSize);
        Assert.True(layout.GptHeaderValid);
        Assert.Equal("Four K", layout.GptPartitions.Single().Name);
        Assert.Equal(32 * MiB / 4096 - 1, layout.GptBackupSector);
    }

    [ToolFact("mkfs.vfat")]
    public void Read_FatVolumeImage_IsNotTreatedAsPartitionedDisk()
    {
        var path = DiskImageBuilder.Create(_dir, "floppy.img", 1440 * 1024);
        ExternalTool.Run("mkfs.vfat", "-F", "12", path);

        var layout = Read(path);

        Assert.True(layout.IsVolumeImage);
        Assert.True(layout.HasMbrSignature);
        Assert.Empty(layout.MbrPartitions);
        Assert.False(layout.IsBootableDisk);
    }

    [Fact]
    public void Read_ApmSignature_IsReported()
    {
        var data = new byte[4096];
        data[0] = (byte)'E';
        data[1] = (byte)'R';
        data[2] = 0x02;

        using var stream = new MemoryStream(data);

        Assert.True(DiskLayoutReader.Read(stream).HasApm);
    }

    [Fact]
    public void Read_EmptyAndTinyInput_YieldsAnEmptyLayout()
    {
        using var empty = new MemoryStream();
        using var tiny = new MemoryStream(new byte[100]);
        using var zeros = new MemoryStream(new byte[64 * 1024]);

        foreach (var stream in new[] { empty, tiny, zeros })
        {
            var layout = DiskLayoutReader.Read(stream);
            Assert.False(layout.HasMbrSignature);
            Assert.False(layout.HasGpt);
            Assert.Empty(layout.MbrPartitions);
        }
    }

    [Fact]
    public void Read_GarbageTableWithSignature_ListsEntriesWithATypeOrASize()
    {
        var data = new byte[512];
        data[510] = 0x55;
        data[511] = 0xAA;
        data[446 + 4] = 0x83;
        data[446 + 8] = 8;
        data[446 + 12] = 100;
        data[462 + 4] = 0x07;

        // The third entry is completely empty and must not show up.

        using var stream = new MemoryStream(data);
        var layout = DiskLayoutReader.Read(stream);

        Assert.Equal(2, layout.MbrPartitions.Count);
        var partition = layout.MbrPartitions[0];
        Assert.Equal((byte)0x83, partition.Type);
        Assert.Equal(8, partition.StartSector);
        Assert.Equal(100, partition.SectorCount);
        Assert.Equal(108, partition.EndSector);
        Assert.Equal(0, partition.Slot);
    }
}
