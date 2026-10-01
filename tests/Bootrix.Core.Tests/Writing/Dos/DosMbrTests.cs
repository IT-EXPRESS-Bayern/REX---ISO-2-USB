// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Bootrix.Core.Boot.Dos;
using Bootrix.Core.Errors;
using Bootrix.Core.Model;
using Bootrix.Core.Partitioning;
using Bootrix.Core.Tests.Tooling;

namespace Bootrix.Core.Tests.Writing.Dos;

public class DosMbrTests
{
    private static byte[] Sector(byte status = 0x80, byte type = MbrPartitionType.Fat16, byte fill = 0xCC)
    {
        var sector = new byte[512];
        sector.AsSpan(0, 440).Fill(fill);
        BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(440), 0xCAFEBABE);
        sector[446] = status;
        sector[450] = type;
        BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(454), 2048);
        BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(458), 100_000);
        sector[510] = 0x55;
        sector[511] = 0xAA;
        return sector;
    }

    [Fact]
    public void Install_ReplacesTheBootCode_AndKeepsSignatureAndTable()
    {
        var before = Sector();

        var after = DosMbr.Install(before);

        Assert.Equal(DosMbr.Bootstrap(), after[..DosMbr.Bootstrap().Length]);
        Assert.Equal(before[440..], after[440..]);
        Assert.Equal(512, after.Length);
    }

    [Fact]
    public void Install_ClearsLeftoversOfOlderBootCode()
    {
        var bootstrap = DosMbr.Bootstrap();

        var after = DosMbr.Install(Sector());

        Assert.All(after[bootstrap.Length..440], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Install_DoesNotChangeItsInput()
    {
        var before = Sector();
        var copy = (byte[])before.Clone();

        DosMbr.Install(before);

        Assert.Equal(copy, before);
    }

    [Fact]
    public void Bootstrap_FitsTheBootCodeAreaOfAnMbr_AndTheForcedDriveVariantDiffers()
    {
        Assert.InRange(DosMbr.Bootstrap().Length, 100, Mbr.BootstrapLength);
        Assert.NotEqual(DosMbr.Bootstrap(), DosMbr.Bootstrap(forceBootDrive: true));
    }

    [Theory]
    [InlineData(MbrPartitionType.Fat12)]
    [InlineData(MbrPartitionType.Fat16Small)]
    [InlineData(MbrPartitionType.Fat16)]
    [InlineData(MbrPartitionType.Fat16Lba)]
    [InlineData(MbrPartitionType.Fat32Chs)]
    [InlineData(MbrPartitionType.Fat32Lba)]
    public void Install_AcceptsEveryFatType(byte type)
    {
        Assert.Equal(512, DosMbr.Install(Sector(type: type)).Length);
    }

    [Fact]
    public void Install_RefusesATableWithoutActivePartition()
    {
        var ex = Assert.Throws<BootrixException>(() => DosMbr.Install(Sector(status: 0)));

        Assert.Equal(ErrorCode.DosMbrNotBootable, ex.Code);
    }

    [Fact]
    public void Install_RefusesTwoActivePartitions()
    {
        var sector = Sector();
        sector[462] = 0x80;
        sector[466] = MbrPartitionType.Fat16;
        BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(470), 200_000);
        BinaryPrimitives.WriteUInt32LittleEndian(sector.AsSpan(474), 1000);

        Assert.Equal(ErrorCode.DosMbrNotBootable, Assert.Throws<BootrixException>(() => DosMbr.Install(sector)).Code);
    }

    [Theory]
    [InlineData(MbrPartitionType.Ntfs)]
    [InlineData(MbrPartitionType.Linux)]
    [InlineData(MbrPartitionType.GptProtective)]
    public void Install_RefusesAnActivePartitionThatIsNoFatVolume(byte type)
    {
        Assert.Equal(ErrorCode.DosMbrNotBootable, Assert.Throws<BootrixException>(() => DosMbr.Install(Sector(type: type))).Code);
    }

    [Fact]
    public void Install_RefusesASectorWithoutSignature()
    {
        var sector = Sector();
        sector[511] = 0;

        Assert.Equal(ErrorCode.DosMbrNotBootable, Assert.Throws<BootrixException>(() => DosMbr.Install(sector)).Code);
    }

    [RequiresToolFact("sfdisk")]
    public void ImageOfAStick_HasOneBootablePartitionOfTheFatType_AccordingToSfdisk()
    {
        var plan = DosImageBuilder.PlanStick(64 * DosImageBuilder.Mib, FileSystemKind.Fat16);
        using var image = DosImageBuilder.BuildStick(plan, FreeDosSystem.Create());

        var dump = ExternalTools.Run("sfdisk", "--dump", image.Path).Output;

        Assert.Contains("label: dos", dump, StringComparison.Ordinal);
        Assert.Contains("label-id: 0x42445258", dump, StringComparison.Ordinal);
        Assert.Matches(@"start=\s*2048, size=\s*\d+, type=6, bootable", dump);
    }

    [RequiresToolFact("sfdisk")]
    public void ImageOfAStick_HasThePartitionAtTheClassicOffset_WhenLegacyFixesAreOn()
    {
        var plan = DosImageBuilder.PlanStick(64 * DosImageBuilder.Mib, FileSystemKind.Fat16, legacy: true);
        using var image = DosImageBuilder.BuildStick(plan, FreeDosSystem.Create());

        var dump = ExternalTools.Run("sfdisk", "--dump", image.Path).Output;

        Assert.Matches(@"start=\s*128, size=\s*\d+, type=6, bootable", dump);
    }
}
