// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.FileSystems.Fat;
using Bootrix.Core.Model;
using Bootrix.Core.Partitioning;
using Bootrix.Core.Planning;
using Bootrix.Core.Profiles;
using Bootrix.Core.Tests.Images.Support;
using Bootrix.Core.Tests.Tooling;
using Bootrix.Core.Writing.Windows;

namespace Bootrix.Core.Tests.Writing.Windows.BootCode;

public class WindowsMbrTests
{
    private static readonly TargetOptions BiosOnly = new() { Firmware = TargetFirmware.Bios };

    private static byte[] Sector0(TempImage image, int sectorSize = 512)
    {
        using var stream = image.Open();
        var sector = new byte[sectorSize];
        stream.ReadExactly(sector);
        return sector;
    }

    private static void WriteSector0(TempImage image, byte[] sector)
    {
        using var stream = image.Open();
        stream.Write(sector);
    }

    [Theory]
    [InlineData(TargetFirmware.Bios, true)]
    [InlineData(TargetFirmware.BiosAndUefi, true)]
    [InlineData(TargetFirmware.Uefi, false)]
    public void TheMbrNeedsBootCodeOnlyForABiosStart(TargetFirmware firmware, bool expected)
    {
        var plan = TestMedium.Plan(new TargetOptions { Firmware = firmware, Scheme = firmware == TargetFirmware.Uefi ? PartitionScheme.Gpt : PartitionScheme.Mbr });

        Assert.Equal(expected, WindowsMbr.IsNeeded(plan));
    }

    [Fact]
    public void ABiosStartOnGpt_GetsNoMbrCode()
    {
        var plan = TestMedium.Plan(new TargetOptions { Firmware = TargetFirmware.Bios, Scheme = PartitionScheme.Gpt });

        Assert.Equal(PartitionScheme.Gpt, plan.Scheme);
        Assert.False(WindowsMbr.IsNeeded(plan));
    }

    [Fact]
    public void TheBootstrapIsTheSyslinuxMbr_ExactlyAsShipped()
    {
        var shipped = File.ReadAllBytes(FindAsset("syslinux-mbr/mbr.bin"));

        Assert.Equal(440, WindowsMbr.Bootstrap().Length);
        Assert.Equal(shipped, WindowsMbr.Bootstrap());
    }

    [Fact]
    public void Apply_ReplacesTheBootCode_AndKeepsSignatureAndTable()
    {
        var plan = TestMedium.Plan(BiosOnly);
        using var image = TestMedium.Realize(plan);
        var sector = Sector0(image);
        var tableBefore = sector[440..];

        var changed = WindowsMbr.Apply(sector, plan);

        Assert.False(changed);
        Assert.Equal(WindowsMbr.Bootstrap(), sector[..440]);
        Assert.Equal(TestMedium.DiskSignature, BitConverter.ToUInt32(sector, 440));
        Assert.Equal(tableBefore, sector[440..]);
    }

    [Fact]
    public void Apply_PutsTheBootFlagAndTheTypeBackThatTheFormatterMayHaveChanged()
    {
        var plan = TestMedium.Plan(BiosOnly);
        var main = plan.Partitions.Single();
        using var image = TestMedium.Realize(plan);
        var sector = Sector0(image);

        // What FormatEx has been seen to do: drop the LBA variant of the type and, on some Windows builds, the flag.
        sector[446] = 0x00;
        sector[446 + 4] = MbrPartitionType.Fat32Chs;

        var changed = WindowsMbr.Apply(sector, plan);

        Assert.True(changed);
        Assert.Equal(MbrEntry.ActiveStatus, sector[446]);
        Assert.Equal(main.MbrType, sector[446 + 4]);
    }

    [Fact]
    public void Apply_ClearsAnotherActivePartition()
    {
        var plan = TestMedium.Plan(BiosOnly);
        using var image = TestMedium.Realize(plan);
        var sector = Sector0(image);
        sector[446 + 16] = MbrEntry.ActiveStatus;
        sector[446 + 16 + 4] = MbrPartitionType.Linux;
        sector[446 + 16 + 8] = 0xFF;
        sector[446 + 16 + 12] = 0x10;

        WindowsMbr.Apply(sector, plan);

        Assert.Equal(MbrEntry.ActiveStatus, sector[446]);
        Assert.Equal(0, sector[446 + 16]);
        Assert.Equal(MbrPartitionType.Linux, sector[446 + 16 + 4]);
    }

    [Fact]
    public void Apply_SetsTheUefiNtfsPartitionToTheEfiType_AndLeavesItInactive()
    {
        var plan = TestMedium.Plan(new TargetOptions { Firmware = TargetFirmware.BiosAndUefi, FileSystem = FileSystemKind.Ntfs }, deviceBytes: 512 * TestMedium.Mib);
        Assert.True(plan.UsesUefiNtfs);
        using var image = TestMedium.Realize(plan);
        var sector = Sector0(image);
        sector[446 + 16 + 4] = 0x07;
        sector[446 + 16] = MbrEntry.ActiveStatus;

        WindowsMbr.Apply(sector, plan);

        Assert.Equal(MbrEntry.ActiveStatus, sector[446]);
        Assert.Equal(MbrPartitionType.EfiSystem, sector[446 + 16 + 4]);
        Assert.Equal(0, sector[446 + 16]);
    }

    [Fact]
    public void Apply_RefusesATableThatLacksThePlannedPartition()
    {
        var plan = TestMedium.Plan(BiosOnly);
        var sector = new byte[512];
        sector[510] = 0x55;
        sector[511] = 0xAA;

        var ex = Assert.Throws<BootrixException>(() => WindowsMbr.Apply(sector, plan));

        Assert.Equal(ErrorCode.LayoutRejected, ex.Code);
    }

    [Fact]
    public void Apply_RefusesASectorWithoutTheBootSignature()
    {
        var plan = TestMedium.Plan(BiosOnly);

        var ex = Assert.Throws<BootrixException>(() => WindowsMbr.Apply(new byte[512], plan));

        Assert.Equal(ErrorCode.LayoutRejected, ex.Code);
    }

    [Fact]
    public void Apply_OnAFourKibSector_LeavesTheRestOfTheSectorAlone()
    {
        var plan = TestMedium.Plan(BiosOnly, deviceBytes: 1024 * TestMedium.Mib, sectorSize: 4096);
        using var image = TestMedium.Realize(plan);
        var sector = Sector0(image, 4096);
        Array.Fill(sector, (byte)0xA5, 512, 4096 - 512);

        WindowsMbr.Apply(sector, plan);

        Assert.Equal(WindowsMbr.Bootstrap(), sector[..440]);
        Assert.All(sector[512..], value => Assert.Equal(0xA5, value));
    }

    [RequiresToolFact("sfdisk")]
    public void Apply_ProducesATableThatSfdiskReadsAsBootableFat32Lba()
    {
        var plan = TestMedium.Plan(BiosOnly);
        using var image = TestMedium.Realize(plan);
        var sector = Sector0(image);
        sector[446] = 0;
        sector[446 + 4] = MbrPartitionType.Fat32Chs;
        WriteSector0(image, sector);
        Assert.DoesNotMatch(@"type=c, bootable", TestMedium.Sfdisk(image.Path));

        WindowsMbr.Apply(sector, plan);
        WriteSector0(image, sector);

        var dump = TestMedium.Sfdisk(image.Path);
        Assert.Matches(@"start=\s*2048, size=\s*\d+, type=c, bootable", dump);
        Assert.Contains("label-id: 0x5eed1234", dump, StringComparison.Ordinal);
    }

    // --- the code actually runs: SeaBIOS starts the MBR, the MBR starts the volume's boot sector ----------

    [RequiresToolFact(QemuScreen.Tool, "nasm")]
    public void TheMbrStartsTheBootSectorOfTheActivePartition_WhichReadsItsOwnReservedSectors()
    {
        using var dir = new TestDirectory("bootrix-mbr");
        var vbr = TestMedium.AssembleTestVbr(dir);
        var plan = TestMedium.Plan(BiosOnly);
        using var image = TestMedium.Realize(plan, (_, options) => options with { BootCode = vbr });
        var sector = Sector0(image);
        WindowsMbr.Apply(sector, plan);
        WriteSector0(image, sector);

        var screen = QemuScreen.Boot($"-drive file={image.Path},format=raw,if=ide", "VBR SECTOR 12 REACHED", TimeSpan.FromSeconds(30));

        Assert.Contains("VBR SECTOR 12 REACHED", screen, StringComparison.Ordinal);
    }

    [RequiresToolFact(QemuScreen.Tool, "nasm")]
    public void WithoutTheBootFlag_TheSyslinuxCodeDoesNotStartTheVolume()
    {
        using var dir = new TestDirectory("bootrix-mbr");
        var vbr = TestMedium.AssembleTestVbr(dir);
        var plan = TestMedium.Plan(BiosOnly);
        var inactive = plan with { Partitions = [.. plan.Partitions.Select(p => p with { Active = false })] };
        using var image = TestMedium.Realize(inactive, (_, options) => options with { BootCode = vbr });
        var sector = Sector0(image);
        WindowsMbr.Apply(sector, inactive);
        WriteSector0(image, sector);

        var screen = QemuScreen.Boot($"-drive file={image.Path},format=raw,if=ide", "VBR SECTOR 12 REACHED", TimeSpan.FromSeconds(12));

        Assert.DoesNotContain("VBR SECTOR 12 REACHED", screen, StringComparison.Ordinal);
    }

    private static string FindAsset(string relative)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "assets", "third-party", relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException(relative);
    }
}
