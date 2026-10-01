// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Partitioning;
using Bootrix.Core.Tests.Tooling;

namespace Bootrix.Core.Tests.Partitioning;

/// <summary>Creates GPTs with our builder and lets gptfdisk and sfdisk verify them, including the backup at the end of the disk.</summary>
public class GptToolTests
{
    private const long Mib = 1024 * 1024;
    private static readonly Guid DiskId = new("0F1E2D3C-4B5A-6978-8796-A5B4C3D2E1F0");

    [RequiresToolFact("sgdisk", "sfdisk")]
    public void Build_Output_IsValidAccordingToSgdiskAndDescribedBySfdisk()
    {
        using var image = new TempImage(256 * Mib);
        var sectors = 256 * Mib / 512;
        var esp = new Guid("00112233-4455-6677-8899-AABBCCDDEEFF");
        var builder = new GptBuilder(sectors)
            .WithDiskGuid(DiskId)
            .AddPartition(GptTypes.EfiSystem, 2048, 411_647, "EFI System", GptAttributes.RequiredPartition, esp)
            .AddPartition(GptTypes.BasicData, 411_648, 450_000, "Stick Daten", GptAttributes.NoDriveLetter)
            .AddPartition(GptTypes.LinuxData, 450_048, 500_000, "linux");
        Write(image, builder);

        var verify = ExternalTools.Run("sgdisk", "--verify", image.Path);
        Assert.Equal(0, verify.ExitCode);
        Assert.Contains("No problems found", verify.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Problem", verify.Output, StringComparison.Ordinal);

        var dump = ExternalTools.Run("sfdisk", "--dump", image.Path).Output;
        Assert.Contains("label: gpt", dump, StringComparison.Ordinal);
        Assert.Contains($"label-id: {DiskId.ToString().ToUpperInvariant()}", dump, StringComparison.Ordinal);
        Assert.Contains("first-lba: 34", dump, StringComparison.Ordinal);
        Assert.Contains($"last-lba: {sectors - 34}", dump, StringComparison.Ordinal);
        Assert.Matches(@"start=\s*2048, size=\s*409600, type=C12A7328-F81F-11D2-BA4B-00A0C93EC93B, uuid=00112233-4455-6677-8899-AABBCCDDEEFF, name=""EFI System""", dump);
        Assert.Matches(@"start=\s*411648, size=\s*38353, type=EBD0A0A2-B9E5-4433-87C0-68B6B72699C7, .*name=""Stick Daten""", dump);
        Assert.Matches(@"type=0FC63DAF-8483-4772-8E79-3D69D8477DE4, .*name=""linux""", dump);
    }

    [RequiresToolTheory("sgdisk")]
    [InlineData("EfiSystem", "EFI system partition")]
    [InlineData("MicrosoftReserved", "Microsoft reserved")]
    [InlineData("BasicData", "Microsoft basic data")]
    [InlineData("WindowsRecovery", "Windows RE")]
    [InlineData("LinuxData", "Linux filesystem")]
    [InlineData("BiosBoot", "BIOS boot partition")]
    [InlineData("LinuxSwap", "Linux swap")]
    [InlineData("LinuxLvm", "Linux LVM")]
    [InlineData("AppleHfsPlus", "Apple HFS/HFS+")]
    [InlineData("AppleApfs", "Apple APFS")]
    [InlineData("AppleBoot", "Recovery HD")]
    [InlineData("AppleUfs", "Apple UFS")]
    [InlineData("AppleRaid", "Apple RAID")]
    [InlineData("AppleCoreStorage", "Apple Core Storage")]
    [InlineData("AppleApfsPreboot", "Apple APFS Pre-Boot")]
    [InlineData("AppleApfsRecovery", "Apple APFS Recovery")]
    [InlineData("FreeBsdBoot", "FreeBSD boot")]
    public void TypeGuid_IsRecognisedBySgdiskByItsName(string property, string expectedName)
    {
        var type = (Guid)typeof(GptTypes).GetProperty(property)!.GetValue(null)!;
        using var image = new TempImage(64 * Mib);
        Write(image, new GptBuilder(131_072).AddPartition(type, 2048, 4095));

        var info = ExternalTools.Run("sgdisk", "--info=1", image.Path);

        Assert.Equal(0, info.ExitCode);
        Assert.Contains($"({expectedName})", info.Output, StringComparison.Ordinal);
    }

    [RequiresToolFact("sgdisk")]
    public void Attributes_AndNames_SurviveSgdiskReadingThemBack()
    {
        using var image = new TempImage(64 * Mib);
        const GptAttributes all = GptAttributes.RequiredPartition | GptAttributes.NoBlockIoProtocol
            | GptAttributes.LegacyBiosBootable | GptAttributes.ReadOnly | GptAttributes.Hidden | GptAttributes.NoDriveLetter;
        Write(image, new GptBuilder(131_072).AddPartition(GptTypes.BasicData, 2048, 4095, "Größe-€-日本", all));

        var info = ExternalTools.Run("sgdisk", "--info=1", image.Path).Output;

        Assert.Contains("Attribute flags: D000000000000007", info, StringComparison.Ordinal);
        Assert.Contains("Partition name: 'Größe-€-日本'", info, StringComparison.Ordinal);
    }

    [RequiresToolFact("sgdisk")]
    public void Build_Output_HasAProtectiveMbrAndTheBackupAtTheEndOfTheDisk()
    {
        using var image = new TempImage(64 * Mib);
        Write(image, new GptBuilder(131_072).AddPartition(GptTypes.BasicData, 2048, 4095));

        var scan = ExternalTools.Run("gdisk", "-l", image.Path).Output;

        Assert.Contains("MBR: protective", scan, StringComparison.Ordinal);
        Assert.Contains("GPT: present", scan, StringComparison.Ordinal);
        Assert.Contains("Found valid GPT with protective MBR; using GPT.", scan, StringComparison.Ordinal);
        Assert.Contains("last usable sector is 131038", scan, StringComparison.Ordinal);
    }

    [RequiresToolFact("sgdisk")]
    public void Build_Output_HasACompleteBackupCopyAtTheEndOfTheDisk()
    {
        using var image = new TempImage(64 * Mib);
        Write(image, new GptBuilder(131_072).AddPartition(GptTypes.BasicData, 2048, 4095));
        using (var stream = image.Open())
        {
            stream.Position = 512;
            stream.Write(new byte[512]);
        }

        var verify = ExternalTools.Run("sgdisk", "--verify", image.Path).Combined;

        Assert.Contains("Main header: ERROR", verify, StringComparison.Ordinal);
        Assert.Contains("Backup header: OK", verify, StringComparison.Ordinal);
        Assert.Contains("Backup partition table: OK", verify, StringComparison.Ordinal);
        Assert.Contains("No problems found", verify, StringComparison.Ordinal);
    }

    [RequiresToolFact("fdisk")]
    public void FourKibSectors_AreReadByFdiskWithoutComplaints()
    {
        const long total = 32_768;
        using var image = new TempImage(total * 4096);
        Write(image, new GptBuilder(total, 4096).WithDiskGuid(DiskId)
            .AddPartition(GptTypes.EfiSystem, 256, 4351, "efi")
            .AddPartition(GptTypes.LinuxData, 4352, 32_511, "four k"));

        var listing = ExternalTools.Run("fdisk", "-b", "4096", "-l", image.Path);

        Assert.True(listing.ExitCode == 0, listing.Combined);
        Assert.Contains("Units: sectors of 1 * 4096 = 4096 bytes", listing.Output, StringComparison.Ordinal);
        Assert.Contains("Disklabel type: gpt", listing.Output, StringComparison.Ordinal);
        Assert.Contains($"Disk identifier: {DiskId.ToString().ToUpperInvariant()}", listing.Output, StringComparison.Ordinal);
        Assert.Matches(@"\.img1\s+256\s+4351\s+4096\s+16M EFI System", listing.Output);
        Assert.Matches(@"\.img2\s+4352\s+32511\s+28160\s+110M Linux filesystem", listing.Output);
        Assert.DoesNotContain("mismatch", listing.Combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("corrupt", listing.Combined, StringComparison.OrdinalIgnoreCase);
    }

    [RequiresToolFact("sgdisk")]
    public void Build_OnAMultiTerabyteDisk_ClampsTheProtectiveMbrAndKeepsTheBackup()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        const long tebibytes = 3L * 1024 * 1024 * Mib;
        using var image = new TempImage(tebibytes);
        var sectors = tebibytes / 512;
        Write(image, new GptBuilder(sectors).AddPartition(GptTypes.BasicData, 2048, sectors - 2048));

        var verify = ExternalTools.Run("sgdisk", "--verify", image.Path);
        Assert.Contains("No problems found", verify.Output, StringComparison.Ordinal);

        using var stream = image.Open();
        var mbr = new byte[512];
        stream.ReadExactly(mbr);
        var parsed = Mbr.Parse(mbr);
        Assert.Equal(uint.MaxValue, parsed.Entries[0].SectorCount);
        Assert.Equal(ChsAddress.ProtectiveEnd, parsed.Entries[0].LastChs);
        Assert.NotNull(Gpt.Read(stream, 512));
    }

    [RequiresToolFact("sgdisk")]
    public void Read_ParsesATableCreatedBySgdisk()
    {
        using var image = new TempImage(64 * Mib);
        var create = ExternalTools.Run(
            "sgdisk", "--clear",
            "--new=1:2048:+10M", "--typecode=1:EF00", "--change-name=1:Boot Partition",
            "--new=2:0:+20M", "--typecode=2:8300", "--change-name=2:rootfs", "--attributes=2:set:2",
            "--new=3:0:0", "--typecode=3:0700",
            "--disk-guid=7A6B5C4D-3E2F-1A0B-9C8D-7E6F5A4B3C2D",
            image.Path);
        Assert.Equal(0, create.ExitCode);

        using var stream = image.Open();
        var gpt = Gpt.Read(stream, 512);

        Assert.NotNull(gpt);
        Assert.True(gpt.PrimaryValid);
        Assert.True(gpt.BackupValid);
        Assert.Equal(new Guid("7A6B5C4D-3E2F-1A0B-9C8D-7E6F5A4B3C2D"), gpt.DiskGuid);
        Assert.Equal(131_072, gpt.TotalSectors);
        Assert.Equal(3, gpt.Partitions.Count);
        Assert.Equal(GptTypes.EfiSystem, gpt.Partitions[0].TypeGuid);
        Assert.Equal(2048, gpt.Partitions[0].FirstLba);
        Assert.Equal(22_527, gpt.Partitions[0].LastLba);
        Assert.Equal("Boot Partition", gpt.Partitions[0].Name);
        Assert.Equal(GptTypes.LinuxData, gpt.Partitions[1].TypeGuid);
        Assert.Equal(GptAttributes.LegacyBiosBootable, gpt.Partitions[1].Attributes);
        Assert.Equal(GptTypes.BasicData, gpt.Partitions[2].TypeGuid);
        Assert.Equal(131_038, gpt.Partitions[2].LastLba);
    }

    [RequiresToolFact("fdisk")]
    public void Read_ParsesAFourKibTableCreatedByFdisk()
    {
        const long total = 32_768;
        using var image = new TempImage(total * 4096);
        var created = ExternalTools.RunWithInput("fdisk", "g\nn\n1\n256\n+16M\nt\n1\nn\n2\n\n\nw\n", "-b", "4096", image.Path);
        Assert.True(created.ExitCode == 0, created.Combined);

        using var stream = image.Open();
        var gpt = Gpt.Read(stream, 4096);

        Assert.NotNull(gpt);
        Assert.True(gpt.PrimaryValid);
        Assert.True(gpt.BackupValid);

        // fdisk starts the usable area at 1 MiB; the end is where the backup array begins.
        Assert.Equal(256, gpt.FirstUsableLba);
        Assert.Equal(total - 6, gpt.LastUsableLba);
        Assert.Equal([256L, 4352L], gpt.Partitions.Select(p => p.FirstLba));
        Assert.Equal([4351L, 32_511L], gpt.Partitions.Select(p => p.LastLba));
        Assert.Equal(GptTypes.EfiSystem, gpt.Partitions[0].TypeGuid);
        Assert.Equal(GptTypes.LinuxData, gpt.Partitions[1].TypeGuid);
    }

    private static void Write(TempImage image, GptBuilder builder)
    {
        using var stream = image.Open();
        builder.Build().WriteTo(stream);
    }
}
