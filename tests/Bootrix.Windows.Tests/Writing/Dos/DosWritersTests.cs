// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Boot.Dos;
using Bootrix.Core.Errors;
using Bootrix.Core.Images;
using Bootrix.Core.Jobs;
using Bootrix.Core.Model;
using Bootrix.Core.Planning;
using Bootrix.Core.Profiles;
using Bootrix.Core.Storage;
using Bootrix.Core.Writing;
using Bootrix.Windows.Storage;
using Bootrix.Windows.Writing;
using Bootrix.Windows.Writing.Dos;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Windows.Tests.Writing.Dos;

/// <summary>The choice of writer and the step lists; nothing here touches a disk, so these run on any Windows machine.</summary>
public class DosWritersTests
{
    private const long Mib = 1024 * 1024;

    private static readonly ImageProfile DosImage = new() { Kind = ImageKind.Dos };
    private static readonly ImageProfile UnknownImage = new() { Kind = ImageKind.Unknown };

    private static readonly WriteServices Services = new(null!, new DiskPreparer(), new JobJournal(Path.GetTempPath()), NullLoggerFactory.Instance);

    private static StorageDevice Stick(long bytes) => new()
    {
        DiskNumber = 3,
        DevicePath = @"\\?\usbstor#disk&ven_test",
        Serial = "S123",
        SizeBytes = bytes,
        Bus = BusType.Usb,
        IsRemovableMedia = true,
    };

    private static MediaWriteContext Context(ImageProfile image, TargetOptions target, long bytes, JobSpec? spec = null, bool floppy = false)
    {
        var device = Stick(bytes) with { IsFloppy = floppy };
        var plan = LayoutPlanner.Plan(image, target, MediaPlanService.CapsOf(device));
        return new MediaWriteContext
        {
            JobId = "write-test",
            ImagePath = @"C:\images\dos.img",
            Inspection = new ImageInspection { Profile = image, Container = ImageContainer.Unknown },
            Spec = spec ?? new JobSpec { Target = target },
            Targets = [new MediaWriteTarget { Device = device, Identity = DiskIdentity.From(device), Plan = plan }],
            WorkDirectory = Path.GetTempPath(),
        };
    }

    [Fact]
    public void TheDosWriterTakesDosImages_AndTheFormatWriterTakesUnknownOnes()
    {
        var dosPlan = Context(DosImage, new TargetOptions(), 64 * Mib).Plan;
        var dataPlan = Context(UnknownImage, new TargetOptions(), 64 * Mib).Plan;

        Assert.True(new DosWriter(Services).CanWrite(dosPlan, DosImage));
        Assert.False(new DosWriter(Services).CanWrite(dataPlan, UnknownImage));
        Assert.True(new FormatOnlyWriter(Services).CanWrite(dataPlan, UnknownImage));
        Assert.False(new FormatOnlyWriter(Services).CanWrite(dosPlan, DosImage));
    }

    [Fact]
    public void ADosStick_IsCheckedPartitionedFilledAndGetsItsMbr()
    {
        var context = Context(DosImage, new TargetOptions(), 64 * Mib);

        var steps = new DosWriter(Services).CreateSteps(context);

        Assert.Equal(
            [DosSteps.PrepareSystemKey, StandardSteps.CheckKey, StandardSteps.PrepareKey, DosSteps.CopyFilesKey, DosSteps.WriteMbrKey, StandardSteps.FinishKey],
            steps.Select(s => s.Key));
    }

    [Fact]
    public void ADiskette_IsWrittenAsAWholeAndHasNoPartitionStep()
    {
        var context = Context(DosImage, new TargetOptions(), 1_474_560, floppy: true);

        var steps = new DosWriter(Services).CreateSteps(context);

        Assert.True(context.Plan.Superfloppy);
        Assert.Equal(
            [DosSteps.PrepareSystemKey, StandardSteps.CheckKey, DosSteps.SuperfloppyKey, StandardSteps.FinishKey],
            steps.Select(s => s.Key));
    }

    [Fact]
    public void AFormattedDataStick_IsCheckedPartitionedAndFinished()
    {
        var context = Context(UnknownImage, new TargetOptions { Label = "DATA" }, 64 * Mib);

        var steps = new FormatOnlyWriter(Services).CreateSteps(context);

        Assert.Equal([StandardSteps.CheckKey, StandardSteps.PrepareKey, StandardSteps.FinishKey], steps.Select(s => s.Key));
    }

    [Fact]
    public void AFormattedDiskette_NeedsNoDosSystem()
    {
        var context = Context(UnknownImage, new TargetOptions(), 1_474_560, floppy: true);

        var steps = new FormatOnlyWriter(Services).CreateSteps(context);

        Assert.Equal([StandardSteps.CheckKey, DosSteps.SuperfloppyKey, StandardSteps.FinishKey], steps.Select(s => s.Key));
    }

    [Fact]
    public async Task PreparingTheSystem_ForFreeDos_NeedsNoNetworkAndFillsEveryTarget()
    {
        var context = Context(DosImage, new TargetOptions(), 64 * Mib);
        var state = new DosJobState();

        await state.PrepareAsync(context, null!, CancellationToken.None);

        var system = state.SystemOf(context.Targets[0]);
        Assert.Equal(DosFlavor.FreeDos, system.Flavor);
        Assert.Equal("\\KERNEL.SYS", system.Files[0].Path);
    }

    [Fact]
    public async Task MsDosOnFat32_IsRefusedWhileThePlanIsStillOnPaper()
    {
        var context = Context(
            DosImage,
            new TargetOptions(),
            8L * 1024 * Mib,
            new JobSpec { Dos = new DosOptions { Flavor = DosFlavor.MsDos, AcceptMicrosoftDownload = true } });
        var state = new DosJobState();

        var ex = await Assert.ThrowsAsync<BootrixException>(() => state.PrepareAsync(context, null!, CancellationToken.None));

        Assert.Equal(ErrorCode.FileSystemUnsupported, ex.Code);
    }

    [Fact]
    public void TheFormatterHook_FindsItsTargetByThePartitionOfItsPlan()
    {
        var first = Context(DosImage, new TargetOptions(), 64 * Mib);
        var second = Context(DosImage, new TargetOptions(), 128 * Mib);
        var both = new MediaWriteContext
        {
            JobId = first.JobId,
            ImagePath = first.ImagePath,
            Inspection = first.Inspection,
            Spec = first.Spec,
            Targets = [first.Targets[0], second.Targets[0]],
            WorkDirectory = first.WorkDirectory,
        };

        Assert.Same(second.Targets[0], DosJobState.TargetOf(both, second.Plan.Partitions[0]));
        Assert.Same(first.Targets[0], DosJobState.TargetOf(both, first.Plan.Partitions[0]));
    }
}
