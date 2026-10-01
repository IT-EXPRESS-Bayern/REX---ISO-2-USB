// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Model;
using Bootrix.Core.Partitioning;
using Bootrix.Core.Tests.Tooling;

namespace Bootrix.Core.Tests.Partitioning;

public class DiskLayoutWriterTests
{
    private const long Mib = 1024 * 1024;
    private const long TotalSectors = 131_072;

    private static readonly Guid DiskId = new("DEADBEEF-0000-4000-8000-000000000001");

    private static DiskLayout Layout(PartitionScheme scheme, params PartitionEntry[] partitions) => new()
    {
        Scheme = scheme,
        TotalSectors = TotalSectors,
        Partitions = partitions,
        MbrSignature = 0x0BADF00D,
        DiskGuid = DiskId,
    };

    private static PartitionEntry Fat(long start, long count, bool active = false) => new()
    {
        StartLba = start,
        SectorCount = count,
        MbrType = MbrPartitionType.Fat32Lba,
        GptType = GptTypes.BasicData,
        Name = "stick",
        Active = active,
    };

    [Fact]
    public void WriteToStream_Mbr_StoresPartitionsSignatureAndBootstrap()
    {
        var disk = new SparseMemoryStream(TotalSectors * 512);
        var layout = Layout(PartitionScheme.Mbr, Fat(2048, 100_000, active: true)) with { Bootstrap = [0xEB, 0xFE] };

        DiskLayoutWriter.WriteToStream(disk, layout, 512);

        var mbr = ReadMbr(disk);
        Assert.Equal(0x0BADF00Du, mbr.DiskSignature);
        Assert.Equal([0xEB, 0xFE, 0x00], mbr.Bootstrap.Span[..3].ToArray());
        Assert.Equal(MbrPartitionType.Fat32Lba, mbr.Entries[0].Type);
        Assert.True(mbr.Entries[0].IsActive);
        Assert.Equal(2048u, mbr.Entries[0].StartLba);
        Assert.Equal(100_000u, mbr.Entries[0].SectorCount);
        Assert.Null(Gpt.Read(disk, 512));
    }

    [Fact]
    public void WriteToStream_Mbr_UsesTheLayoutsGeometryForChs()
    {
        var disk = new SparseMemoryStream(TotalSectors * 512);
        var layout = Layout(PartitionScheme.Mbr, Fat(63, 100_000)) with { Geometry = new ChsGeometry(16, 63) };

        DiskLayoutWriter.WriteToStream(disk, layout, 512);

        Assert.Equal(new ChsAddress(0, 1, 1), ReadMbr(disk).Entries[0].FirstChs);
    }

    [Fact]
    public void WriteToStream_Gpt_StoresTableBackupAndProtectiveMbr()
    {
        var disk = new SparseMemoryStream(TotalSectors * 512);
        var layout = Layout(PartitionScheme.Gpt, Fat(2048, 20_480) with { UniqueId = DiskId, Attributes = GptAttributes.NoDriveLetter });

        DiskLayoutWriter.WriteToStream(disk, layout, 512);

        var gpt = Gpt.Read(disk, 512);
        Assert.NotNull(gpt);
        Assert.True(gpt.PrimaryValid && gpt.BackupValid);
        Assert.Equal(DiskId, gpt.DiskGuid);
        Assert.Equal(new GptPartition(GptTypes.BasicData, DiskId, 2048, 22_527, GptAttributes.NoDriveLetter, "stick"), gpt.Partitions.Single());
        var mbr = ReadMbr(disk);
        Assert.True(mbr.IsProtective);
        Assert.Equal(0u, mbr.DiskSignature);
    }

    [Fact]
    public void WriteToStream_Gpt_WithBootstrap_PutsTheCodeIntoTheProtectiveMbr()
    {
        var disk = new SparseMemoryStream(TotalSectors * 512);
        var layout = Layout(PartitionScheme.Gpt, Fat(2048, 20_480)) with { Bootstrap = [0xFA, 0xF4] };

        DiskLayoutWriter.WriteToStream(disk, layout, 512);

        var mbr = ReadMbr(disk);
        Assert.Equal([0xFA, 0xF4], mbr.Bootstrap.Span[..2].ToArray());
        Assert.True(mbr.IsProtective);
    }

    [Fact]
    public void WriteToStream_Gpt_WithHybridPartitions_WritesAHybridMbr()
    {
        var disk = new SparseMemoryStream(TotalSectors * 512);
        var layout = Layout(
            PartitionScheme.Gpt,
            Fat(2048, 20_480) with { MbrType = MbrPartitionType.EfiSystem },
            Fat(22_528, 40_960) with { MbrType = MbrPartitionType.Ntfs }) with
        {
            HybridPartitions = [1],
        };

        DiskLayoutWriter.WriteToStream(disk, layout, 512);

        var mbr = ReadMbr(disk);
        Assert.Equal(MbrPartitionType.GptProtective, mbr.Entries[0].Type);
        Assert.Equal(22_527u, mbr.Entries[0].SectorCount);
        Assert.Equal(MbrPartitionType.Ntfs, mbr.Entries[1].Type);
        Assert.Equal(22_528u, mbr.Entries[1].StartLba);
        Assert.Equal(2, Gpt.Read(disk, 512)!.Partitions.Count);
    }

    [Fact]
    public void WriteToStream_FourKibSectors_PlacesTheMbrInTheFirstSector()
    {
        const long total = 16_384;
        var disk = new SparseMemoryStream(total * 4096);
        var layout = Layout(PartitionScheme.Gpt, Fat(256, 8000)) with { TotalSectors = total };

        DiskLayoutWriter.WriteToStream(disk, layout, 4096);

        Assert.NotNull(Gpt.Read(disk, 4096));
        var first = new byte[4096];
        disk.Position = 0;
        disk.ReadExactly(first);
        Assert.True(Mbr.Parse(first).IsProtective);
        Assert.All(first[512..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void WriteToStream_Rejects_UnsuitableInput()
    {
        var shortDisk = new SparseMemoryStream(1024 * 1024);

        Assert.Throws<ArgumentException>(() => DiskLayoutWriter.WriteToStream(shortDisk, Layout(PartitionScheme.Mbr, Fat(2048, 100)), 512));
        Assert.Throws<ArgumentException>(() => DiskLayoutWriter.WriteToStream(
            new SparseMemoryStream(TotalSectors * 512), Layout(PartitionScheme.Auto, Fat(2048, 100)), 512));
        Assert.Throws<ArgumentNullException>(() => DiskLayoutWriter.WriteToStream(null!, Layout(PartitionScheme.Mbr), 512));
    }

    [RequiresToolFact("sfdisk", "sgdisk")]
    public void WrittenLayouts_AreAcceptedByTheFremdwerkzeuge()
    {
        using var mbrImage = new TempImage(64 * Mib);
        using (var stream = mbrImage.Open())
        {
            DiskLayoutWriter.WriteToStream(
                stream, Layout(PartitionScheme.Mbr, Fat(2048, 40_000, active: true), Fat(50_000, 30_000) with { MbrType = MbrPartitionType.Ntfs }), 512);
        }

        var dump = ExternalTools.Run("sfdisk", "--dump", mbrImage.Path).Output;
        Assert.Contains("label-id: 0x0badf00d", dump, StringComparison.Ordinal);
        Assert.Matches(@"start=\s*2048, size=\s*40000, type=c, bootable", dump);
        Assert.Matches(@"start=\s*50000, size=\s*30000, type=7", dump);

        using var gptImage = new TempImage(64 * Mib);
        using (var stream = gptImage.Open())
        {
            DiskLayoutWriter.WriteToStream(stream, Layout(PartitionScheme.Gpt, Fat(2048, 20_480), Fat(22_528, 40_960)), 512);
        }

        var verify = ExternalTools.Run("sgdisk", "--verify", gptImage.Path);
        Assert.Contains("No problems found", verify.Output, StringComparison.Ordinal);
    }

    [RequiresToolFact(QemuScreen.Tool)]
    public void MbrWithoutBootablePartition_ShowsTheMissingOperatingSystemMessageInQemu()
    {
        using var image = new TempImage(64 * Mib);
        using (var stream = image.Open())
        {
            DiskLayoutWriter.WriteToStream(stream, Layout(PartitionScheme.Mbr, Fat(2048, 100_000)), 512);
        }

        var screen = QemuScreen.Boot($"-drive file={image.Path},format=raw,if=ide", "Missing operating system", TimeSpan.FromSeconds(15));

        Assert.Contains("Missing operating system", screen, StringComparison.Ordinal);
    }

    private static Mbr ReadMbr(Stream disk)
    {
        var sector = new byte[512];
        disk.Position = 0;
        disk.ReadExactly(sector);
        return Mbr.Parse(sector);
    }
}
