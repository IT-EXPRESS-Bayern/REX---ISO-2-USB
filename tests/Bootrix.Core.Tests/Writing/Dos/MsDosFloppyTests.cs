// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Boot.Dos;
using Bootrix.Core.Errors;
using Bootrix.Core.FileSystems.Fat;
using Bootrix.Core.Model;
using Bootrix.Core.Partitioning;
using Bootrix.Core.Tests.FileSystems.Fat;
using Bootrix.Core.Tests.Tooling;
using Bootrix.Core.Writing.Dos;

namespace Bootrix.Core.Tests.Writing.Dos;

/// <summary>The diskette logic with generated stand-in files; the real ones are only used by <see cref="MsDosRealFilesTests"/> when somebody has them.</summary>
public class MsDosFloppyTests
{
    private const long Mib = DosImageBuilder.Mib;

    private static DosSystem StandIn() => MsDosFloppy.ToSystem(MsDosFixtures.Floppy());

    [Fact]
    public void ToSystem_PutsTheSystemFilesFirst_WithTheirAttributes()
    {
        var system = StandIn();

        Assert.Equal(["\\IO.SYS", "\\MSDOS.SYS", "\\COMMAND.COM"], system.Files.Take(3).Select(f => f.Path));
        Assert.Equal(DosFile.SystemFile, system.Files[0].Attributes);
        Assert.Equal(DosFile.SystemFile, system.Files[1].Attributes);
        Assert.Equal(FileAttributes.Archive, system.Files[2].Attributes);
        Assert.Equal(DosFlavor.MsDos, system.Flavor);
    }

    [Fact]
    public void ToSystem_CopiesTheLocaleToolsIntoTheirOwnFolder()
    {
        var system = StandIn();

        var locale = system.Files.Where(f => f.Folder == "\\LOCALE").Select(f => Path.GetFileName(f.Path.Replace('\\', '/'))).ToList();
        Assert.Equal(MsDosFixtures.LocaleNames.Order(), locale.Order());
    }

    [Fact]
    public void ToSystem_SkipsLocaleToolsThatAreNotOnTheDiskette()
    {
        var system = MsDosFloppy.ToSystem(MsDosFixtures.Floppy(withLocaleFiles: false));

        Assert.DoesNotContain(system.Files, f => f.Folder == "\\LOCALE");
    }

    [Fact]
    public void ToSystem_TurnsTheTwoConditionalJumpsIntoUnconditionalOnes_AndTouchesNothingElse()
    {
        var system = StandIn();
        var ioSys = system.Files[0].Content;
        var commandCom = system.Files[2].Content;
        var originalIo = MsDosFixtures.IoSys();
        var originalCommand = MsDosFixtures.CommandCom();

        Assert.Equal(0xEB, ioSys[MsDosFixtures.IoSysPatchAt]);
        Assert.Equal(0xEB, commandCom[MsDosFixtures.CommandPatchAt]);
        Assert.Equal(originalIo.Where((b, i) => i != MsDosFixtures.IoSysPatchAt), ioSys.Where((b, i) => i != MsDosFixtures.IoSysPatchAt));
        Assert.Equal(originalCommand.Where((b, i) => i != MsDosFixtures.CommandPatchAt), commandCom.Where((b, i) => i != MsDosFixtures.CommandPatchAt));
    }

    [Fact]
    public void ToSystem_RefusesAnIoSysItDoesNotKnow()
    {
        var other = MsDosFixtures.IoSys();
        other[MsDosFixtures.IoSysPatchAt] = 0x74;

        var ex = Assert.Throws<BootrixException>(() => MsDosFloppy.ToSystem(MsDosFixtures.Floppy(ioSys: other)));

        Assert.Equal(ErrorCode.MsDosImageInvalid, ex.Code);
    }

    [Fact]
    public void ToSystem_RefusesAnIoSysOfAnotherLength()
    {
        var ex = Assert.Throws<BootrixException>(() => MsDosFloppy.ToSystem(MsDosFixtures.Floppy(ioSys: MsDosFixtures.IoSys()[..100_000])));

        Assert.Equal(ErrorCode.MsDosImageInvalid, ex.Code);
    }

    [Fact]
    public void ToSystem_RefusesACommandComItDoesNotKnow()
    {
        var other = MsDosFixtures.CommandCom();
        other[0x650E] = 0x00;

        var ex = Assert.Throws<BootrixException>(() => MsDosFloppy.ToSystem(MsDosFixtures.Floppy(commandCom: other)));

        Assert.Equal(ErrorCode.MsDosImageInvalid, ex.Code);
    }

    [Fact]
    public void ToSystem_RefusesAnImageOfTheWrongSize()
    {
        Assert.Equal(ErrorCode.MsDosImageInvalid, Assert.Throws<BootrixException>(() => MsDosFloppy.ToSystem(new byte[1000])).Code);
    }

    [Fact]
    public void Customize_TransplantsTheBootCodeOfTheDiskette()
    {
        var system = StandIn();

        var options = system.Customize(new FatFormatOptions { TotalBytes = 64 * Mib, Type = FatType.Fat16, HiddenSectors = 2048, DriveNumber = 0x80 }, 131_072);

        // The formatter writes the BPB (bytes 3 to 0x3D) itself; jump and code are what the diskette brings.
        var expected = MsDosFixtures.BootSector();
        Assert.Equal(512, options.BootCode!.Length);
        Assert.Equal(expected[..3], options.BootCode[..3]);
        Assert.Equal(expected[0x3E..], options.BootCode[0x3E..]);
        Assert.Equal(2048u, options.HiddenSectors);
    }

    [Theory]
    [InlineData(131_072, 16)]
    [InlineData(2_000_000, 32)]
    [InlineData(3_900_000, 64)]
    [InlineData(8_000_000, 128)]
    [InlineData(20_000_000, 255)]
    public void Customize_GivesTheBpbTheGeometryAnOldBiosPicksForTheDisk(long diskSectors, int heads)
    {
        var options = StandIn().Customize(new FatFormatOptions { TotalBytes = 64 * Mib, Type = FatType.Fat16, DriveNumber = 0x80 }, diskSectors);

        Assert.Equal(heads, options.Heads);
        Assert.Equal(63, options.SectorsPerTrack);
    }

    [Fact]
    public void Customize_KeepsTheGeometryOfADiskette()
    {
        var preset = FloppyPreset.All.Single(p => p.Name == "1.44M");

        var options = StandIn().Customize(preset.ToOptions(), 2880);

        Assert.Equal(18, options.SectorsPerTrack);
        Assert.Equal(2, options.Heads);
    }

    [Fact]
    public void Customize_RefusesFat32()
    {
        var ex = Assert.Throws<BootrixException>(() =>
            StandIn().Customize(new FatFormatOptions { TotalBytes = 600 * Mib, Type = FatType.Fat32 }, 1_200_000));

        Assert.Equal(ErrorCode.FileSystemUnsupported, ex.Code);
    }

    [RequiresToolFact("fsck.vfat", "mdir")]
    public void Stick_WithTheStandInSystem_HasIoSysFirstAndItsBootSectorInPlace()
    {
        var plan = DosImageBuilder.PlanStick(64 * Mib, FileSystemKind.Fat16);
        var system = StandIn();
        using var disk = DosImageBuilder.BuildStick(plan, system);
        var main = plan.Partitions.Single();

        using var stream = disk.Open();
        var boot = new byte[512];
        stream.Position = main.StartBytes;
        stream.ReadExactly(boot);
        var code = MsDosFixtures.BootSector();
        Assert.Equal(code[..3], boot[..3]);
        Assert.Equal(code[0x3E..510], boot[0x3E..510]);
        Assert.Equal((uint)main.StartLba(512), BitConverter.ToUInt32(boot, 0x1C));

        // The data area starts behind two FATs and 32 root directory sectors; IO.SYS must be the first file in it.
        var layout = FatGeometry.Compute(plan.ToFatOptions(main) with { BootCode = null });
        var firstData = main.StartBytes + layout.DataStartSector * 512;
        var head = new byte[16];
        stream.Position = firstData;
        stream.ReadExactly(head);
        Assert.Equal(system.Files[0].Content[..16], head);

        using var volume = new TempImage(main.LengthBytes);
        using (var target = volume.Open())
        {
            stream.Position = main.StartBytes;
            stream.CopyTo(target, 1 << 20);
        }

        _ = FatVerifier.Fsck(volume.Path);
    }

    [RequiresToolFact("fsck.vfat")]
    public void Diskette_WithTheStandInSystem_IsClean()
    {
        var plan = DosImageBuilder.PlanFloppy();
        using var image = new TempImage(plan.DeviceBytes);
        using (var device = new Bootrix.Core.Storage.FileBlockDevice(image.Path, plan.DeviceBytes, create: false))
        {
            SuperfloppyWriter.Write(device, plan, StandIn());
        }

        _ = FatVerifier.Fsck(image.Path);
    }
}
