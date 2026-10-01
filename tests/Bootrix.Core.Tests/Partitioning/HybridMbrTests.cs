// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Partitioning;
using Bootrix.Core.Tests.Tooling;

namespace Bootrix.Core.Tests.Partitioning;

public class HybridMbrTests
{
    private const long Mib = 1024 * 1024;

    private static readonly HybridEntry Data = new(MbrPartitionType.Ntfs, 22_528, 40_960);
    private static readonly HybridEntry Linux = new(MbrPartitionType.Linux, 63_488, 67_551, Active: true);

    [Fact]
    public void Create_PutsTheProtectiveEntryFirstAndCoversEverythingUpToTheFirstMirror()
    {
        var mbr = HybridMbr.Create([Data, Linux], firstUsableLba: 34);

        Assert.Equal(MbrPartitionType.GptProtective, mbr.Entries[0].Type);
        Assert.Equal(1u, mbr.Entries[0].StartLba);
        Assert.Equal(22_527u, mbr.Entries[0].SectorCount);
        Assert.Equal(MbrPartitionType.Ntfs, mbr.Entries[1].Type);
        Assert.Equal(MbrPartitionType.Linux, mbr.Entries[2].Type);
        Assert.True(mbr.Entries[2].IsActive);
        Assert.True(mbr.Entries[3].IsEmpty);
        Assert.True(mbr.IsProtective);
    }

    [Fact]
    public void Create_CanPutTheProtectiveEntryLast()
    {
        var mbr = HybridMbr.Create([Data, Linux], firstUsableLba: 34, protectiveFirst: false);

        Assert.Equal(MbrPartitionType.Ntfs, mbr.Entries[0].Type);
        Assert.Equal(MbrPartitionType.Linux, mbr.Entries[1].Type);
        Assert.Equal(MbrPartitionType.GptProtective, mbr.Entries[2].Type);
        Assert.Single(mbr.Entries, entry => entry.Type == MbrPartitionType.GptProtective);
    }

    [Fact]
    public void Create_KeepsBootstrapSignatureAndGeometry()
    {
        var mbr = HybridMbr.Create(
            [Data], 34, bootstrap: [0xEB, 0xFE], diskSignature: 0xAABBCCDD, geometry: new ChsGeometry(16, 63));

        Assert.Equal(0xAABBCCDDu, mbr.DiskSignature);
        Assert.Equal([0xEB, 0xFE, 0x00], mbr.Bootstrap.Span[..3].ToArray());
        Assert.Equal(new ChsGeometry(16, 63).FromLba(22_528), mbr.Entries[1].FirstChs);
    }

    [Fact]
    public void Create_Rejects_InvalidSelections()
    {
        Assert.Throws<ArgumentException>(() => HybridMbr.Create([], 34));
        Assert.Throws<ArgumentException>(() => HybridMbr.Create([Data, Data, Data, Data], 34));
        Assert.Throws<ArgumentException>(() => HybridMbr.Create([new HybridEntry(MbrPartitionType.GptProtective, 100, 100)], 34));
        Assert.Throws<ArgumentException>(() => HybridMbr.Create([new HybridEntry(MbrPartitionType.Ntfs, 10, 100)], 34));
        Assert.Throws<ArgumentOutOfRangeException>(() => HybridMbr.Create([new HybridEntry(MbrPartitionType.Ntfs, 5_000_000_000, 100)], 34));
    }

    [RequiresToolFact("gdisk", "sgdisk")]
    public void Output_IsRecognisedAsAHybridMbrByGptfdisk()
    {
        using var image = new TempImage(64 * Mib);
        var builder = new GptBuilder(131_072)
            .AddPartition(GptTypes.EfiSystem, 2048, 22_527)
            .AddPartition(GptTypes.BasicData, 22_528, 63_487)
            .AddPartition(GptTypes.LinuxData, 63_488, 131_038);
        builder.WithMbr(HybridMbr.Create(
            [new HybridEntry(MbrPartitionType.Ntfs, 22_528, 40_960), new HybridEntry(MbrPartitionType.Linux, 63_488, 67_551)],
            builder.FirstUsableLba));
        using (var stream = image.Open())
        {
            builder.Build().WriteTo(stream);
        }

        var scan = ExternalTools.Run("gdisk", "-l", image.Path).Output;
        Assert.Contains("MBR: hybrid", scan, StringComparison.Ordinal);
        Assert.Contains("GPT: present", scan, StringComparison.Ordinal);
        Assert.Contains("Found valid GPT with hybrid MBR; using GPT.", scan, StringComparison.Ordinal);

        var verify = ExternalTools.Run("sgdisk", "--verify", image.Path);
        Assert.Contains("No problems found", verify.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Problem", verify.Output, StringComparison.Ordinal);
    }

    [RequiresToolFact("sgdisk")]
    public void MirrorEntries_ArePlacedLikeGptfdiskDoes()
    {
        using var reference = new TempImage(64 * Mib);
        var create = ExternalTools.Run(
            "sgdisk", "--clear", "--new=1:2048:+10M", "--typecode=1:EF00", "--new=2:0:+20M", "--typecode=2:0700",
            "--new=3:0:0", "--typecode=3:8300", reference.Path);
        Assert.Equal(0, create.ExitCode);
        Assert.Equal(0, ExternalTools.Run("sgdisk", "--hybrid=2:3", reference.Path).ExitCode);

        using var stream = reference.Open();
        var sector = new byte[512];
        stream.ReadExactly(sector);
        var theirs = Mbr.Parse(sector);

        var ours = HybridMbr.Create(
            [new HybridEntry(MbrPartitionType.Ntfs, 22_528, 40_960), new HybridEntry(MbrPartitionType.Linux, 63_488, 67_551)],
            firstUsableLba: 34);

        Assert.Equal(theirs.Entries[0].Type, ours.Entries[0].Type);
        Assert.Equal(theirs.Entries[0].StartLba, ours.Entries[0].StartLba);
        Assert.Equal(theirs.Entries[0].SectorCount, ours.Entries[0].SectorCount);
        Assert.Equal((theirs.Entries[1].StartLba, theirs.Entries[1].SectorCount), (ours.Entries[1].StartLba, ours.Entries[1].SectorCount));
        Assert.Equal((theirs.Entries[2].StartLba, theirs.Entries[2].SectorCount), (ours.Entries[2].StartLba, ours.Entries[2].SectorCount));
        Assert.Equal(theirs.Entries[1].FirstChs, ours.Entries[1].FirstChs);
    }
}
