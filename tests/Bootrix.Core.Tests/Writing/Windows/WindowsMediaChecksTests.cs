// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images;
using Bootrix.Core.Model;
using Bootrix.Core.Planning;
using Bootrix.Core.Profiles;
using Bootrix.Core.Tests.Writing.Windows.BootCode;
using Bootrix.Core.Writing.Windows;

namespace Bootrix.Core.Tests.Writing.Windows;

public class WindowsMediaChecksTests
{
    private static ImageInspection Inspection(WindowsArch arch, params WindowsArch[] loaders) => new()
    {
        Profile = TestMedium.SmallWindowsImage(arch),
        Container = ImageContainer.IsoUdfBridge,
        EfiArchitectures = loaders,
    };

    private static MediaPlan Plan(TargetFirmware firmware) =>
        TestMedium.Plan(new TargetOptions { Firmware = firmware, Scheme = firmware == TargetFirmware.Uefi ? PartitionScheme.Gpt : PartitionScheme.Auto });

    [Fact]
    public void AMatchingImage_HasNothingToReport()
    {
        Assert.Empty(BootArchitectureCheck.Findings(Inspection(WindowsArch.X64, WindowsArch.X64), Plan(TargetFirmware.BiosAndUefi)));
        Assert.Empty(BootArchitectureCheck.Findings(Inspection(WindowsArch.X64, WindowsArch.X86, WindowsArch.X64), Plan(TargetFirmware.Uefi)));
    }

    [Fact]
    public void AnImageWithOnlyA32BitLoader_IsReportedForUefiMedia()
    {
        var findings = BootArchitectureCheck.Findings(Inspection(WindowsArch.X86, WindowsArch.X86), Plan(TargetFirmware.Uefi));

        Assert.Contains("32-bit UEFI loader", Assert.Single(findings), StringComparison.Ordinal);
    }

    [Fact]
    public void ABiosOnlyMedium_NeedsNoEfiLoader()
    {
        Assert.Empty(BootArchitectureCheck.Findings(Inspection(WindowsArch.X86, WindowsArch.X86), Plan(TargetFirmware.Bios)));
    }

    [Fact]
    public void AnArm64OnlyLoader_IsReported()
    {
        var findings = BootArchitectureCheck.Findings(Inspection(WindowsArch.Arm64, WindowsArch.Arm64), Plan(TargetFirmware.Uefi));

        Assert.Contains("ARM64", Assert.Single(findings), StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsForAnArchitectureWithoutALoader_IsReported()
    {
        var findings = BootArchitectureCheck.Findings(Inspection(WindowsArch.X64, WindowsArch.X86, WindowsArch.Arm64), Plan(TargetFirmware.Uefi));

        Assert.Contains("X64", Assert.Single(findings), StringComparison.Ordinal);
    }

    [Fact]
    public void Arm64WithABiosBootSector_IsReported()
    {
        var findings = BootArchitectureCheck.Findings(Inspection(WindowsArch.Arm64, WindowsArch.Arm64), Plan(TargetFirmware.BiosAndUefi));

        Assert.Contains(findings, finding => finding.Contains("no BIOS", StringComparison.Ordinal));
    }

    [Fact]
    public void AnImageWithoutAnyLoaderInfo_IsNotJudged()
    {
        Assert.Empty(BootArchitectureCheck.Findings(Inspection(WindowsArch.Unknown), Plan(TargetFirmware.Uefi)));
    }

    // --- progress and space ----------------------------------------------------------------------------------

    [Fact]
    public void Targets_ShareTheBarInEqualParts()
    {
        var progress = new TargetSequenceProgress(3, 1000);

        Assert.Equal(3000, progress.TotalBytes);
        Assert.Equal(0, progress.Overall(0, 0));
        Assert.Equal(500, progress.Overall(0, 500));
        Assert.Equal(1000, progress.Overall(1, 0));
        Assert.Equal(1750, progress.Overall(1, 750));
        Assert.Equal(3000, progress.Overall(2, 1000));
    }

    [Fact]
    public void ATargetCannotLeaveItsShare()
    {
        var progress = new TargetSequenceProgress(2, 1000);

        Assert.Equal(1000, progress.Overall(0, 1500));
        Assert.Equal(1000, progress.Overall(1, -5));
    }

    [Fact]
    public void TargetsOutsideTheRangeAreProgrammingErrors()
    {
        var progress = new TargetSequenceProgress(2, 10);

        Assert.Throws<ArgumentOutOfRangeException>(() => progress.Overall(2, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => progress.Overall(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TargetSequenceProgress(0, 10));
    }

    [Fact]
    public void FatSpace_CountsWholeClustersAndOneClusterPerDirectory()
    {
        var plan = WindowsCopyPlan.Create(
            [new MediaSourceFile("a.bin", 1), new MediaSourceFile("sources/b.bin", 4096), new MediaSourceFile("sources/c.bin", 4097), new MediaSourceFile("empty.txt", 0)],
            [],
            new WindowsCopyOptions());

        // a: 4096, b: 4096, c: 8192, empty: 0, directory "sources": 4096
        Assert.Equal(4096 + 4096 + 8192 + 4096, plan.BytesOnFat(4096));
    }

    [Fact]
    public void FatSpace_CountsASplitImageWithItsOverhead()
    {
        var plan = WindowsCopyPlan.Create(
            [new MediaSourceFile("sources/install.wim", 6L << 30)],
            [],
            new WindowsCopyOptions { MaxFileBytes = WindowsCopyOptions.Fat32MaxFileBytes, SplitInstallImage = true });

        var bytes = plan.BytesOnFat(32768);

        Assert.InRange(bytes, 6L << 30, (6L << 30) + (100L << 20));
        Assert.Equal(0, bytes % 32768);
    }
}
