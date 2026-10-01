// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Boot.Dos;
using Bootrix.Core.Errors;
using Bootrix.Core.Images;
using Bootrix.Core.Model;
using Bootrix.Core.Planning;
using Bootrix.Core.Profiles;
using Bootrix.Core.Tests.Planning;
using Bootrix.Core.Writing.Dos;

namespace Bootrix.Core.Tests.Writing.Dos;

public class DosMediumTests
{
    private const long Mib = DosImageBuilder.Mib;

    private static MediaPlan Plan(ImageProfile image, DeviceCaps device, TargetOptions? target = null) =>
        LayoutPlanner.Plan(image, target ?? new TargetOptions(), device);

    [Fact]
    public void ADosImage_OnAStick_IsADosMedium_AndNotAPlainFormat()
    {
        var image = PlannerFixtures.Dos();
        var plan = Plan(image, PlannerFixtures.Stick(64 * Mib));

        Assert.True(DosMedium.IsDosMedium(plan, image));
        Assert.False(DosMedium.IsPlainFormat(plan, image));
    }

    [Fact]
    public void AnUnknownImage_IsAPlainFormat_AndNotADosMedium()
    {
        var image = PlannerFixtures.Unknown();
        var plan = Plan(image, PlannerFixtures.Stick(64 * Mib));

        Assert.True(DosMedium.IsPlainFormat(plan, image));
        Assert.False(DosMedium.IsDosMedium(plan, image));
    }

    [Theory]
    [InlineData("win")]
    [InlineData("linux-iso")]
    [InlineData("data")]
    public void ImagesThatAreCopiedFileByFile_AreNeitherOfThem(string key)
    {
        var image = PlannerFixtures.ByKey(key);
        var plan = Plan(image, PlannerFixtures.Stick(16 * 1024 * Mib));

        Assert.False(DosMedium.IsDosMedium(plan, image));
        Assert.False(DosMedium.IsPlainFormat(plan, image));
    }

    [Fact]
    public void ARawImage_IsNeitherOfThem()
    {
        var image = PlannerFixtures.RawDisk();
        var plan = Plan(image, PlannerFixtures.Stick(4 * 1024 * Mib));

        Assert.False(DosMedium.IsDosMedium(plan, image));
        Assert.False(DosMedium.IsPlainFormat(plan, image));
    }

    [Fact]
    public void ADosImage_OnADiskette_IsADisketteMedium()
    {
        var image = PlannerFixtures.Dos();
        var plan = Plan(image, PlannerFixtures.Floppy());

        Assert.True(DosMedium.IsDosMedium(plan, image));
        Assert.True(DosMedium.IsDiskette(plan));
    }

    [Fact]
    public void ASuperfloppyStick_IsNoDiskette_UnlessItHasAFloppySize()
    {
        var stick = Plan(PlannerFixtures.Dos(), PlannerFixtures.Stick(64 * Mib), new TargetOptions { Superfloppy = true });
        var small = Plan(PlannerFixtures.Dos(), PlannerFixtures.Stick(1_474_560), new TargetOptions { Superfloppy = true });

        Assert.True(stick.Superfloppy);
        Assert.False(DosMedium.IsDiskette(stick));
        Assert.True(DosMedium.IsDiskette(small));
        Assert.False(DosMedium.IsDiskette(Plan(PlannerFixtures.Dos(), PlannerFixtures.Stick(64 * Mib))));
    }

    [Fact]
    public void FreeDos_OnAStick_GetsThe386KernelWithLba()
    {
        var plan = Plan(PlannerFixtures.Dos(), PlannerFixtures.Stick(64 * Mib));

        var system = DosMedium.CreateSystem(new DosOptions(), plan, null);

        Assert.Equal(DosFlavor.FreeDos, system.Flavor);
        Assert.Equal(1, system.Files[0].Content[0x0D]);
        Assert.Equal(DosAssets.FreeDos("KERNL386.SYS").Length, system.Files[0].Content.Length);
    }

    [Fact]
    public void FreeDos_WithLegacyBiosFixes_LeavesTheKernelAtCautiousSettings()
    {
        var plan = Plan(PlannerFixtures.Dos(), PlannerFixtures.Stick(64 * Mib), new TargetOptions { LegacyBiosFixes = true });

        var system = DosMedium.CreateSystem(new DosOptions(), plan, null);

        Assert.True(plan.LegacyBios);
        Assert.Equal(0, system.Files[0].Content[0x0D]);
    }

    [Fact]
    public void FreeDos_OnADiskette_GetsTheKernelForAnyCpu()
    {
        var plan = Plan(PlannerFixtures.Dos(), PlannerFixtures.Floppy());

        var system = DosMedium.CreateSystem(new DosOptions(), plan, null);

        Assert.Equal(DosAssets.FreeDos("KERNL86.SYS"), system.Files[0].Content);
    }

    [Fact]
    public void MsDos_WithoutTheDownloadedFile_AsksForTheDownload()
    {
        var plan = Plan(PlannerFixtures.Dos(), PlannerFixtures.Stick(64 * Mib));

        var ex = Assert.Throws<BootrixException>(() => DosMedium.CreateSystem(new DosOptions { Flavor = DosFlavor.MsDos }, plan, null));

        Assert.Equal(ErrorCode.MsDosNotDownloaded, ex.Code);
    }

    [Fact]
    public void MsDos_WithTheFile_BuildsTheSystemFromItsDiskette()
    {
        var plan = Plan(PlannerFixtures.Dos(), PlannerFixtures.Stick(64 * Mib));
        var dll = MsDosFixtures.Dll(MsDosFixtures.Floppy());

        var system = DosMedium.CreateSystem(new DosOptions { Flavor = DosFlavor.MsDos }, plan, dll);

        Assert.Equal(DosFlavor.MsDos, system.Flavor);
        Assert.Equal("\\IO.SYS", system.Files[0].Path);
    }

    [Fact]
    public void MsDos_OnAPlanWithFat32_IsRefusedBeforeAnythingIsWritten()
    {
        var plan = Plan(PlannerFixtures.Dos(), PlannerFixtures.Stick(8 * 1024 * Mib));
        var dll = MsDosFixtures.Dll(MsDosFixtures.Floppy());

        var ex = Assert.Throws<BootrixException>(() => DosMedium.CreateSystem(new DosOptions { Flavor = DosFlavor.MsDos }, plan, dll));

        Assert.Equal(plan.Partitions[0].FileSystem, FileSystemKind.Fat32);
        Assert.Equal(ErrorCode.FileSystemUnsupported, ex.Code);
    }

    [Fact]
    public void MsDos_OnALargeStickWithFat16Requested_UsesTheFat16CapOfDos()
    {
        var plan = Plan(PlannerFixtures.Dos(), PlannerFixtures.Stick(8 * 1024 * Mib), new TargetOptions { FileSystem = FileSystemKind.Fat16 });

        var system = DosMedium.CreateSystem(new DosOptions { Flavor = DosFlavor.MsDos }, plan, MsDosFixtures.Dll(MsDosFixtures.Floppy()));

        Assert.Equal(FileSystemKind.Fat16, plan.Partitions[0].FileSystem);
        Assert.Equal(2048 * Mib, plan.Partitions[0].LengthBytes);
        Assert.NotNull(system);
    }

    [Fact]
    public void TheDosOptionsOfASpec_SurviveAJsonRoundTrip_AndDefaultToFreeDos()
    {
        var spec = new JobSpec { Dos = new DosOptions { Flavor = DosFlavor.MsDos, AcceptMicrosoftDownload = true, Kernel = DosKernel.I8086 } };

        var back = JobSpecJson.Parse(JobSpecJson.Serialize(spec));

        Assert.Equal(spec.Dos, back.Dos);
        Assert.Equal(DosFlavor.FreeDos, JobSpecJson.Parse("{}").Dos.Flavor);
        Assert.False(JobSpecJson.Parse("{}").Dos.AcceptMicrosoftDownload);
    }
}
