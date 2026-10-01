// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;
using Bootrix.Core.Partitioning;
using Bootrix.Core.Tests.Tooling;

namespace Bootrix.Core.Tests.Partitioning;

/// <summary>Writes MBRs with our builder and lets sfdisk and fdisk read them back, and the other way round.</summary>
public partial class MbrToolTests
{
    private const long Mib = 1024 * 1024;

    [RequiresToolFact("sfdisk")]
    public void Build_Output_IsAcceptedAndDescribedBySfdisk()
    {
        using var image = new TempImage(64 * Mib);
        var mbr = new MbrBuilder()
            .WithSignature(0x1A2B3C4D)
            .AddPartition(MbrPartitionType.Fat32Lba, 2048, 40_000, active: true)
            .AddPartition(MbrPartitionType.Ntfs, 50_000, 30_000)
            .AddPartition(MbrPartitionType.Linux, 90_000, 20_000)
            .AddPartition(MbrPartitionType.EfiSystem, 120_000, 4096)
            .Build();
        Write(image, mbr);

        var dump = ExternalTools.Run("sfdisk", "--dump", image.Path);

        Assert.Equal(0, dump.ExitCode);
        Assert.Contains("label: dos", dump.Output, StringComparison.Ordinal);
        Assert.Contains("label-id: 0x1a2b3c4d", dump.Output, StringComparison.Ordinal);
        Assert.Matches(@"start=\s*2048, size=\s*40000, type=c, bootable", dump.Output);
        Assert.Matches(@"start=\s*50000, size=\s*30000, type=7", dump.Output);
        Assert.Matches(@"start=\s*90000, size=\s*20000, type=83", dump.Output);
        Assert.Matches(@"start=\s*120000, size=\s*4096, type=ef", dump.Output);
        Assert.Equal(0, ExternalTools.Run("sfdisk", "--verify", image.Path).ExitCode);
    }

    [RequiresToolFact("fdisk")]
    public void Build_ChsValues_AreDecodedByFdiskAsExpected()
    {
        using var image = new TempImage(64 * Mib);
        var mbr = new MbrBuilder()
            .AddPartition(MbrPartitionType.Fat32Lba, 2048, 100_000, active: true)
            .AddPartition(MbrPartitionType.Linux, 110_000, 20_000)
            .Build();
        Write(image, mbr);

        var chs = FdiskChs(image.Path);

        Assert.Equal(2, chs.Count);
        Assert.Equal((new ChsAddress(0, 32, 33), new ChsAddress(6, 89, 51)), chs[0]);
        Assert.Equal((new ChsAddress(6, 216, 3), new ChsAddress(8, 23, 31)), chs[1]);
        Assert.Equal(mbr.Entries[0].LastChs, chs[0].Last);
        Assert.Equal(mbr.Entries[1].FirstChs, chs[1].First);
    }

    [RequiresToolFact("fdisk")]
    public void Build_PartitionBeyondTheChsRange_ShowsTheClampedAddressInFdisk()
    {
        using var image = new TempImage(10L * 1024 * Mib);
        Write(image, new MbrBuilder().AddPartition(MbrPartitionType.Ntfs, 2048, 20_000_000).Build());

        var chs = FdiskChs(image.Path);

        Assert.Equal((new ChsAddress(0, 32, 33), ChsAddress.Unrepresentable), chs.Single());
    }

    [RequiresToolFact("sfdisk")]
    public void Parse_ReadsAnMbrCreatedBySfdisk()
    {
        using var image = new TempImage(64 * Mib);
        var script = "label: dos\nlabel-id: 0xCAFEBABE\nunit: sectors\n\n"
            + "start=2048, size=20480, type=c, bootable\n"
            + "start=22528, size=40960, type=83\n"
            + "start=63488, size=8192, type=7\n";
        var result = ExternalTools.RunWithInput("sfdisk", script, "--quiet", image.Path);
        Assert.Equal(0, result.ExitCode);

        var mbr = Mbr.Parse(File.ReadAllBytes(image.Path).AsSpan(0, 512));

        Assert.Equal(0xCAFEBABEu, mbr.DiskSignature);
        Assert.Equal((MbrPartitionType.Fat32Lba, 2048u, 20480u, true), Describe(mbr.Entries[0]));
        Assert.Equal((MbrPartitionType.Linux, 22528u, 40960u, false), Describe(mbr.Entries[1]));
        Assert.Equal((MbrPartitionType.Ntfs, 63488u, 8192u, false), Describe(mbr.Entries[2]));
        Assert.True(mbr.Entries[3].IsEmpty);

        // The CHS bytes sfdisk chose are the ones our geometry computes.
        Assert.Equal(ChsGeometry.Translated.FromLba(2048), mbr.Entries[0].FirstChs);
    }

    [RequiresToolFact("sfdisk")]
    public void ProtectiveMbr_IsRecognisedAsGptBySfdisk()
    {
        using var image = new TempImage(64 * Mib);
        using (var stream = image.Open())
        {
            stream.Write(MbrBuilder.Protective(131_072).ToBytes());
        }

        var listing = ExternalTools.Run("sfdisk", "--list", image.Path);

        // Without a GPT behind it the protective entry is all sfdisk can report.
        Assert.Contains("ee", listing.Output, StringComparison.OrdinalIgnoreCase);
    }

    private static (byte Type, uint Start, uint Size, bool Active) Describe(MbrEntry entry) =>
        (entry.Type, entry.StartLba, entry.SectorCount, entry.IsActive);

    private static void Write(TempImage image, Mbr mbr)
    {
        using var stream = image.Open();
        stream.Write(mbr.ToBytes());
    }

    /// <summary>fdisk's expert listing prints the raw start and end CHS bytes of every entry.</summary>
    private static List<(ChsAddress First, ChsAddress Last)> FdiskChs(string imagePath)
    {
        var result = ExternalTools.RunWithInput("fdisk", "x\np\nq\n", imagePath);
        var entries = new List<(ChsAddress, ChsAddress)>();
        foreach (var line in result.Output.Split('\n'))
        {
            var matches = ChsPair().Matches(line);
            if (matches.Count == 2)
            {
                entries.Add((ToAddress(matches[0]), ToAddress(matches[1])));
            }
        }

        return entries;
    }

    private static ChsAddress ToAddress(Match match) => new(
        int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
        int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
        int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture));

    [GeneratedRegex(@"(\d+)/(\d+)/(\d+)")]
    private static partial Regex ChsPair();
}
