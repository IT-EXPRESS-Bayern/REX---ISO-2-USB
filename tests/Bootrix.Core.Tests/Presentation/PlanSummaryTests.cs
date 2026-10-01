// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bootrix.Core.Images;
using Bootrix.Core.Localization;
using Bootrix.Core.Model;
using Bootrix.Core.Planning;
using Bootrix.Core.Presentation;
using Bootrix.Core.Profiles;
using Bootrix.Core.Storage;
using Bootrix.Core.Writing;

namespace Bootrix.Core.Tests.Presentation;

public class PlanSummaryTests
{
    private static readonly Localizer English = new() { Culture = CultureInfo.GetCultureInfo("en-US") };

    private static StorageDevice Stick(long size = 16L << 30) => new()
    {
        DiskNumber = 2,
        DevicePath = @"\\?\usbstor#disk",
        Bus = BusType.Usb,
        IsRemovableMedia = true,
        SizeBytes = size,
    };

    private static WritePreview Preview(ImageProfile profile, TargetOptions? target = null, IReadOnlyList<ImageWarning>? warnings = null)
    {
        var inspection = new ImageInspection { Profile = profile, Container = ImageContainer.IsoUdfBridge, Warnings = warnings ?? [] };
        return MediaPlanService.Plan(inspection, target ?? new TargetOptions(), Stick());
    }

    private static ImageProfile WindowsIso(long largest = 5L << 30) => new()
    {
        Kind = ImageKind.WindowsSetup,
        VolumeLabel = "CCCOMA_X64FRE_DE-DE_DV9",
        TotalBytes = 6L << 30,
        LargestFileBytes = largest,
        HasBiosBootFiles = true,
        HasEfiBootFiles = true,
        Arch = WindowsArch.X64,
        WindowsBuild = 26100,
    };

    [Fact]
    public void WindowsIsoOnAStickIsDescribedWithImageMethodSchemeAndBoot()
    {
        var summary = PlanSummary.From(Preview(WindowsIso()), English);

        var byLabel = summary.Lines.ToDictionary(l => l.Label, l => l.Value);
        Assert.Contains("Windows installation media", byLabel["Image"], StringComparison.Ordinal);
        Assert.Contains("X64", byLabel["Image"], StringComparison.Ordinal);
        Assert.Contains("Build 26100", byLabel["Image"], StringComparison.Ordinal);
        Assert.Equal("Copy files to a freshly prepared drive", byLabel["Write method"]);
        Assert.Contains("UEFI", byLabel["Boots through"], StringComparison.Ordinal);
        Assert.Contains("Partitions", byLabel.Keys);
    }

    [Fact]
    public void ALargeInstallImageOnFat32MentionsTheSplit()
    {
        var summary = PlanSummary.From(Preview(WindowsIso(), new TargetOptions { FileSystem = FileSystemKind.Fat32 }), English);

        Assert.Contains(summary.Lines, l => l.Value.Contains("split", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AHybridLinuxImageIsWrittenRawAndHasNoOwnPartitionTable()
    {
        var profile = new ImageProfile { Kind = ImageKind.LinuxHybrid, IsHybrid = true, TotalBytes = 3L << 30, ImageBytes = 3L << 30, HasEspPartition = true, HasBiosBootFiles = true, HasEfiBootFiles = true };

        var summary = PlanSummary.From(Preview(profile), English);

        var byLabel = summary.Lines.ToDictionary(l => l.Label, l => l.Value);
        Assert.Equal("Write byte for byte (DD)", byLabel["Write method"]);
        Assert.Equal("from the image", byLabel["Partition table"]);
    }

    [Fact]
    public void WarningsOfTheImageAndOfThePlanAreBothListedWithTheirSeverity()
    {
        var profile = WindowsIso() with { HasEfiBootFiles = false };
        var warnings = new[] { new ImageWarning(ImageWarningKeys.PartialDownload, WarningSeverity.Error, ".part") };

        var summary = PlanSummary.From(Preview(profile, new TargetOptions { Firmware = TargetFirmware.Uefi }, warnings), English);

        Assert.True(summary.HasErrors);
        Assert.Contains(summary.Warnings, w => w.Severity == WarningSeverity.Error);
        Assert.Contains(summary.Warnings, w => w.Text.Contains("UEFI", StringComparison.Ordinal) && w.Severity == WarningSeverity.Warning);
    }

    [Fact]
    public void GermanTextsComeFromTheSameKeys()
    {
        var german = new Localizer { Culture = CultureInfo.GetCultureInfo("de-DE") };

        var summary = PlanSummary.From(Preview(WindowsIso()), german);

        Assert.Contains(summary.Lines, l => l.Label == "Schreibweise");
        Assert.Contains(summary.Lines, l => l.Value.Contains("Windows-Installationsmedium", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ImageKind.WindowsSetup)]
    [InlineData(ImageKind.WindowsPe)]
    [InlineData(ImageKind.LinuxHybrid)]
    [InlineData(ImageKind.LinuxIsoOnly)]
    [InlineData(ImageKind.Bsd)]
    [InlineData(ImageKind.Dos)]
    [InlineData(ImageKind.Apple)]
    [InlineData(ImageKind.RawDisk)]
    [InlineData(ImageKind.Data)]
    [InlineData(ImageKind.OtherOs)]
    [InlineData(ImageKind.Unknown)]
    public void EveryImageKindHasAName(ImageKind kind)
    {
        Assert.True(English.Has("Plan.Kind." + kind), kind.ToString());
    }

    [Theory]
    [InlineData(WriteMethod.RawCopy)]
    [InlineData(WriteMethod.ExtractFiles)]
    [InlineData(WriteMethod.FormatOnly)]
    [InlineData(WriteMethod.ApplyImage)]
    public void EveryWriteMethodHasAText(WriteMethod method)
    {
        Assert.True(English.Has("Plan.Method." + method), method.ToString());
    }

    [Fact]
    public void EveryBootMethodAndPartitionRoleHasAText()
    {
        foreach (var flag in Enum.GetValues<BootMethod>().Where(f => f != BootMethod.None))
        {
            Assert.True(English.Has("Plan.Boot." + flag), flag.ToString());
        }

        foreach (var role in Enum.GetValues<PartitionRole>())
        {
            Assert.True(English.Has("Plan.Role." + role), role.ToString());
        }
    }
}
