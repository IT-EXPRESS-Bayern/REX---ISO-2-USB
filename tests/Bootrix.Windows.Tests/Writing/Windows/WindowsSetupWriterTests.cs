// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images;
using Bootrix.Core.Localization;
using Bootrix.Core.Model;
using Bootrix.Core.Planning;
using Bootrix.Core.Profiles;
using Bootrix.Windows.Jobs;
using Bootrix.Windows.Writing;
using Bootrix.Windows.Writing.Windows;

namespace Bootrix.Windows.Tests.Writing.Windows;

public sealed class WindowsSetupWriterTests : IDisposable
{
    private readonly WindowsWriteKit _kit = new();

    public void Dispose() => _kit.Dispose();

    private WindowsSetupWriter Writer(params IWindowsMediaCustomizer[] customizers) =>
        new(WindowsWriteKit.Services(), new FileImageStreamProvider(), customizers, new FakeBootCode(), new FakeTargetOps());

    private MediaWriteContext Context(MediaPlan plan, bool readBack = true, int targets = 1)
    {
        var list = Enumerable.Range(0, targets).Select(i => _kit.Target(plan, 3 + i)).ToList();
        return _kit.Context(
            "windows.iso",
            new ImageInspection { Profile = WindowsWriteKit.Image(), Container = ImageContainer.IsoUdfBridge },
            list,
            new JobSpec { Verify = new VerifyOptions { ReadBack = readBack } });
    }

    [Theory]
    [InlineData(WriteMethod.ExtractFiles, ImageKind.WindowsSetup, true)]
    [InlineData(WriteMethod.ExtractFiles, ImageKind.WindowsPe, true)]
    [InlineData(WriteMethod.ExtractFiles, ImageKind.LinuxIsoOnly, false)]
    [InlineData(WriteMethod.ExtractFiles, ImageKind.Dos, false)]
    [InlineData(WriteMethod.RawCopy, ImageKind.WindowsSetup, false)]
    [InlineData(WriteMethod.FormatOnly, ImageKind.WindowsSetup, false)]
    [InlineData(WriteMethod.ApplyImage, ImageKind.WindowsSetup, false)]
    public void CanWrite_AcceptsOnlyWindowsMediaInFileCopyMode(WriteMethod method, ImageKind kind, bool expected)
    {
        var plan = new MediaPlan { WriteMethod = method };

        Assert.Equal(expected, Writer().CanWrite(plan, new ImageProfile { Kind = kind }));
    }

    [Fact]
    public void CanWrite_RefusesAMediumWithoutPartitionTable()
    {
        var plan = new MediaPlan { WriteMethod = WriteMethod.ExtractFiles, Superfloppy = true };

        Assert.False(Writer().CanWrite(plan, new ImageProfile { Kind = ImageKind.WindowsSetup }));
    }

    [Fact]
    public void CanWrite_AcceptsTheRealPlansOfTheWindowsPlanner()
    {
        foreach (var firmware in new[] { TargetFirmware.Uefi, TargetFirmware.Bios, TargetFirmware.BiosAndUefi })
        {
            var plan = WindowsWriteKit.Plan(new TargetOptions { Firmware = firmware });

            Assert.True(Writer().CanWrite(plan, WindowsWriteKit.Image()), firmware.ToString());
        }
    }

    [Fact]
    public void TheWriterIsAskedAfterTheRawWriter_AndSelectsItself()
    {
        Assert.Equal("windows-setup", Writer().Id);
    }

    [Fact]
    public void ABiosMediumWithVerification_HasAllTheSteps_InOrder()
    {
        var context = Context(WindowsWriteKit.Plan(new TargetOptions { Firmware = TargetFirmware.BiosAndUefi }));

        var steps = Writer().CreateSteps(context);

        Assert.Equal(
            ["Write.CheckTargets", "Write.Windows.Source", "Write.Prepare", "Write.Windows.Copy", "Write.Windows.BootCode", "Write.Windows.Verify", "Write.Finish"],
            steps.Select(step => step.Key));
    }

    [Fact]
    public void AUefiMediumWithoutVerification_OmitsTheMbrAndTheReadBack()
    {
        var context = Context(WindowsWriteKit.Plan(new TargetOptions { Firmware = TargetFirmware.Uefi }), readBack: false);

        var steps = Writer().CreateSteps(context);

        Assert.Equal(
            ["Write.CheckTargets", "Write.Windows.Source", "Write.Prepare", "Write.Windows.Copy", "Write.Finish"],
            steps.Select(step => step.Key));
    }

    [Fact]
    public void Customizers_GetOneStepBeforeTheEnd_ButOnlyThoseThatApply()
    {
        var context = Context(WindowsWriteKit.Plan(new TargetOptions { Firmware = TargetFirmware.Uefi }));

        var withOne = Writer(new FakeCustomizer("unattend"), new FakeCustomizer("skipped", applies: false)).CreateSteps(context);
        var withNone = Writer(new FakeCustomizer("skipped", applies: false)).CreateSteps(context);

        Assert.Equal(["Write.Windows.Verify", "Write.Customize", "Write.Finish"], withOne.Select(step => step.Key).TakeLast(3));
        Assert.DoesNotContain(withNone, step => step.Key == "Write.Customize");
    }

    [Fact]
    public void TheCopyDominatesTheProgress_AndEveryStepHasATitle()
    {
        var context = Context(WindowsWriteKit.Plan(new TargetOptions { Firmware = TargetFirmware.BiosAndUefi }));
        var steps = Writer(new FakeCustomizer("unattend")).CreateSteps(context);

        Assert.All(steps, step => Assert.True(step.Weight > 0, step.Key));
        Assert.Equal(steps.Max(step => step.Weight), steps.Single(step => step.Key == "Write.Windows.Copy").Weight);
        Assert.All(steps, step => Assert.True(Localizer.Default.Has("Step." + step.Key), step.Key));
    }

    [Fact]
    public void TheWorkerListOfTheEngine_ContainsTheWriter()
    {
        var services = WindowsWriteKit.Services();

        var writers = MediaWriters.CreateDefault(services, new FileImageStreamProvider(), rawWrite: null!);

        Assert.Contains(writers, writer => writer is WindowsSetupWriter);
    }
}
