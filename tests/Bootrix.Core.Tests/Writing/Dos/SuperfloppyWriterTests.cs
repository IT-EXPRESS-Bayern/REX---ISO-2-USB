// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Boot.Dos;
using Bootrix.Core.Errors;
using Bootrix.Core.FileSystems.Fat;
using Bootrix.Core.Model;
using Bootrix.Core.Partitioning;
using Bootrix.Core.Planning;
using Bootrix.Core.Profiles;
using Bootrix.Core.Storage;
using Bootrix.Core.Tests.FileSystems.Fat;
using Bootrix.Core.Tests.Planning;
using Bootrix.Core.Tests.Tooling;
using Bootrix.Core.Writing.Dos;

namespace Bootrix.Core.Tests.Writing.Dos;

public class SuperfloppyWriterTests
{
    private const long Mib = DosImageBuilder.Mib;
    private const string Marker = "BOOTRIX-OK";

    private static DosSystem MarkerSystem(bool floppy = false) =>
        FreeDosSystem.Create(floppy: floppy).WithFile(DosFile.Text("\\AUTOEXEC.BAT", $"@ECHO OFF\nECHO {Marker}\n"));

    /// <summary>A block device over a file that drops every write at or behind <paramref name="failFrom"/>, like a stick that fakes its capacity.</summary>
    private sealed class FaultyDevice(IBlockDevice inner, long failFrom) : IBlockDevice
    {
        public string Name => inner.Name;

        public int SectorSize => inner.SectorSize;

        public long Length => inner.Length;

        public int BufferAlignment => inner.BufferAlignment;

        public void Write(long offset, ReadOnlySpan<byte> data)
        {
            if (offset < failFrom)
            {
                inner.Write(offset, data);
            }
        }

        public int Read(long offset, Span<byte> buffer) => inner.Read(offset, buffer);

        public void Flush() => inner.Flush();

        public void Dispose() => inner.Dispose();
    }

    /// <summary>Reports on the calling thread; <see cref="Progress{T}"/> would post to the thread pool and make the test timing-dependent.</summary>
    private sealed class ImmediateProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }

    private static TempImage WriteFloppy(long bytes, DosSystem? system)
    {
        var plan = DosImageBuilder.PlanFloppy(bytes);
        var image = new TempImage(bytes);
        using var device = new FileBlockDevice(image.Path, bytes, create: false);
        SuperfloppyWriter.Write(device, plan, system);
        return image;
    }

    [RequiresToolTheory("fsck.vfat", "mdir")]
    [InlineData(368_640)]
    [InlineData(737_280)]
    [InlineData(1_228_800)]
    [InlineData(1_474_560)]
    [InlineData(2_949_120)]
    public void Diskette_WithFreeDos_IsCleanAndHoldsTheSystemFiles(long bytes)
    {
        using var image = WriteFloppy(bytes, FreeDosSystem.Create(floppy: true));

        _ = FatVerifier.Fsck(image.Path);

        var listing = ExternalTools.Run("mdir", "-a", "-i", image.Path, "::").Output;
        Assert.Contains("KERNEL", listing, StringComparison.Ordinal);
        Assert.Contains("COMMAND", listing, StringComparison.Ordinal);
    }

    [Fact]
    public void Diskette_UsesTheBpbOfItsFormat()
    {
        using var image = WriteFloppy(1_474_560, FreeDosSystem.Create(floppy: true));

        using var stream = image.Open();
        var boot = new byte[512];
        stream.ReadExactly(boot);
        Assert.Equal(0xF0, boot[0x15]);
        Assert.Equal(18, BitConverter.ToUInt16(boot, 0x18));
        Assert.Equal(2, BitConverter.ToUInt16(boot, 0x1A));
        Assert.Equal(0u, BitConverter.ToUInt32(boot, 0x1C));
        Assert.Equal(0, boot[0x24]);
        Assert.Equal(FreeDosBootSector.CodeFor(FatType.Fat12)[0x3E..510], boot[0x3E..510]);
    }

    [RequiresToolTheory(QemuScreen.Tool)]
    [InlineData(737_280)]
    [InlineData(1_474_560)]
    public void Diskette_BootsFreeDosInQemu(long bytes)
    {
        using var image = WriteFloppy(bytes, MarkerSystem(floppy: true));

        var screen = QemuScreen.Boot($"-drive file={image.Path},format=raw,if=floppy -boot a", Marker, TimeSpan.FromSeconds(60));

        Assert.Contains(Marker, screen, StringComparison.Ordinal);
    }

    [RequiresToolTheory(QemuScreen.Tool, "fsck.vfat")]
    [InlineData(64, FileSystemKind.Fat16)]
    [InlineData(16, FileSystemKind.Fat12)]
    [InlineData(300, FileSystemKind.Fat32)]
    public void StickWithoutPartitionTable_BootsFreeDosFromItsFirstSector(int megabytes, FileSystemKind fileSystem)
    {
        var plan = DosImageBuilder.PlanSuperfloppyStick(megabytes * Mib, fileSystem);
        using var image = new TempImage(plan.DeviceBytes);
        using (var device = new FileBlockDevice(image.Path, plan.DeviceBytes, create: false))
        {
            SuperfloppyWriter.Write(device, plan, MarkerSystem());
        }

        _ = FatVerifier.Fsck(image.Path);
        var screen = QemuScreen.Boot($"-drive file={image.Path},format=raw,if=ide", Marker, TimeSpan.FromSeconds(60));

        Assert.Contains(Marker, screen, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutASystem_TheVolumeIsEmptyAndLabeled()
    {
        var plan = LayoutPlanner.Plan(
            PlannerFixtures.Unknown(),
            new TargetOptions { Superfloppy = true, FileSystem = FileSystemKind.Fat16, Label = "DATA" },
            PlannerFixtures.Stick(64 * Mib));
        using var image = new TempImage(plan.DeviceBytes);
        using (var device = new FileBlockDevice(image.Path, plan.DeviceBytes, create: false))
        {
            SuperfloppyWriter.Write(device, plan, system: null);
        }

        using var stream = image.Open();
        using var fat = new DiscUtils.Fat.FatFileSystem(stream);
        Assert.Equal("DATA", fat.VolumeLabel);
        Assert.Empty(fat.GetFiles("\\"));
        Assert.Equal(512, fat.SectorSize);
    }

    [Fact]
    public void LargeStick_RemovesAnOldBackupGpt_FromTheEndOfTheDevice()
    {
        var plan = DosImageBuilder.PlanSuperfloppyStick(64 * Mib, FileSystemKind.Fat16);
        using var image = new TempImage(plan.DeviceBytes);
        using (var stream = image.Open())
        {
            var gpt = new GptBuilder(plan.TotalSectors, 512).AddPartition(GptTypes.BasicData, 2048, 4095, "old").Build();
            gpt.WriteTo(stream);
        }

        using (var device = new FileBlockDevice(image.Path, plan.DeviceBytes, create: false))
        {
            SuperfloppyWriter.Write(device, plan, FreeDosSystem.Create());
        }

        using var check = image.Open();
        var header = new byte[512];
        check.Position = plan.DeviceBytes - 512;
        check.ReadExactly(header);
        Assert.All(header, b => Assert.Equal(0, b));
        check.Position = 512;
        check.ReadExactly(header);
        Assert.DoesNotContain("EFI PART", System.Text.Encoding.ASCII.GetString(header, 0, 8), StringComparison.Ordinal);
    }

    [Fact]
    public void SmallMedia_AreVerifiedAfterWriting_AndAMismatchIsReported()
    {
        var plan = DosImageBuilder.PlanFloppy();
        using var image = new TempImage(plan.DeviceBytes);
        using var file = new FileBlockDevice(image.Path, plan.DeviceBytes, create: false);
        using var faulty = new FaultyDevice(file, failFrom: 1_000_000);

        var ex = Assert.Throws<BootrixException>(() => SuperfloppyWriter.Write(faulty, plan, FreeDosSystem.Create(floppy: true)));

        Assert.Equal(ErrorCode.VerifyMismatch, ex.Code);
    }

    [Fact]
    public void Progress_ReachesOne()
    {
        var plan = DosImageBuilder.PlanFloppy();
        using var image = new TempImage(plan.DeviceBytes);
        using var device = new FileBlockDevice(image.Path, plan.DeviceBytes, create: false);
        var reports = new List<double>();

        SuperfloppyWriter.Write(device, plan, FreeDosSystem.Create(floppy: true), new ImmediateProgress(reports.Add));

        Assert.NotEmpty(reports);
        Assert.InRange(reports.Max(), 0.99, 1.0);
    }

    [Fact]
    public void Cancellation_StopsTheWrite()
    {
        var plan = DosImageBuilder.PlanFloppy();
        using var image = new TempImage(plan.DeviceBytes);
        using var device = new FileBlockDevice(image.Path, plan.DeviceBytes, create: false);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            SuperfloppyWriter.Write(device, plan, FreeDosSystem.Create(floppy: true), cancellationToken: cancelled.Token));
    }

    [Fact]
    public void APlanWithPartitions_IsRefused()
    {
        var plan = DosImageBuilder.PlanStick(64 * Mib, FileSystemKind.Fat16);
        using var image = new TempImage(plan.DeviceBytes);
        using var device = new FileBlockDevice(image.Path, plan.DeviceBytes, create: false);

        Assert.Throws<InvalidOperationException>(() => SuperfloppyWriter.Write(device, plan, FreeDosSystem.Create()));
    }

    [Fact]
    public void ADeviceThatIsSmallerThanThePlan_IsRefusedAsChanged()
    {
        var plan = DosImageBuilder.PlanFloppy();
        using var image = new TempImage(1_000_000);
        using var device = new FileBlockDevice(image.Path, 1_000_000, create: false);

        var ex = Assert.Throws<BootrixException>(() => SuperfloppyWriter.Write(device, plan, FreeDosSystem.Create(floppy: true)));

        Assert.Equal(ErrorCode.DeviceChanged, ex.Code);
    }
}
