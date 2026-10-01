// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bootrix.Core.Jobs;
using Bootrix.Core.Localization;
using Bootrix.Core.Presentation;
using Bootrix.Core.Storage;

namespace Bootrix.Core.Tests.Presentation;

public class PresentationTests
{
    private static readonly Localizer English = new() { Culture = CultureInfo.GetCultureInfo("en-US") };

    private static StorageDevice Stick(DeviceProtection protection = DeviceProtection.None, string? serial = "0123ABCD") =>
        new()
        {
            DiskNumber = 3,
            DevicePath = @"\\?\usbstor#disk",
            Vendor = "SanDisk",
            Product = "Ultra",
            Serial = serial,
            Bus = BusType.Usb,
            SizeBytes = 15_500_000_000,
            Protection = protection,
        };

    [Fact]
    public void Describe_ShowsNameBusSizeAndSerial()
    {
        var description = DeviceDescription.Describe(Stick(), English);

        Assert.Equal("SanDisk Ultra", description.Title);
        Assert.Contains("USB", description.Details, StringComparison.Ordinal);
        Assert.Contains("Serial 0123ABCD", description.Details, StringComparison.Ordinal);
        Assert.Equal("14.44 GB", description.SizeText);
        Assert.True(description.Selectable);
        Assert.Null(description.Warning);
    }

    [Theory]
    [InlineData(DeviceProtection.SystemDisk, "System disk")]
    [InlineData(DeviceProtection.PagefileDisk, "Holds the page file")]
    [InlineData(DeviceProtection.SystemDisk | DeviceProtection.BootDisk, "System disk, Boot disk")]
    public void Describe_BlockedDisksAreNotSelectableAndSayWhy(DeviceProtection protection, string reason)
    {
        var description = DeviceDescription.Describe(Stick(protection: protection), English);

        Assert.False(description.Selectable);
        Assert.Equal(reason, description.BlockReason);
    }

    [Fact]
    public void Describe_InternalDisksStayUsableButCarryAWarning()
    {
        var description = DeviceDescription.Describe(Stick(protection: DeviceProtection.InternalFixedDisk), English);

        Assert.True(description.Selectable);
        Assert.Equal("Internal disk (service mode required)", description.Warning);
    }

    [Fact]
    public void Describe_ReadOnlyMediaCannotBeSelected()
    {
        var device = Stick() with { IsWritable = false };

        Assert.Equal("Write protected", DeviceDescription.Describe(device, English).BlockReason);
    }

    [Fact]
    public void Describe_DoesNotRepeatAReasonThatIsAlreadyAFlag()
    {
        var device = Stick(protection: DeviceProtection.WriteProtected) with { IsWritable = false };

        Assert.Equal("Write protected", DeviceDescription.Describe(device, English).BlockReason);
    }

    [Fact]
    public void ConfirmationText_IsTheSerialOrTheDiskNumber()
    {
        Assert.Equal("0123ABCD", DeviceConfirmation.TextFor(Stick()));
        Assert.Equal("disk3", DeviceConfirmation.TextFor(Stick(serial: " ")));
        Assert.True(DeviceConfirmation.Matches(Stick(), " 0123abcd "));
        Assert.False(DeviceConfirmation.Matches(Stick(), "0123"));
        Assert.False(DeviceConfirmation.Matches(Stick(), null));
    }

    [Fact]
    public void ProgressView_ShowsStepNumberTitleAndRounding()
    {
        var report = new ProgressReport("j", 2, 4, "Raw.Write", 0.5, 0.4567, 100, 200, 31_457_280, TimeSpan.FromSeconds(252), null);

        var view = ProgressView.From(report, English);

        Assert.Equal("3/4  Writing image", view.Title);
        Assert.Equal(45.7, view.Percent);
        Assert.Equal("30 MB/s", view.Speed);
        Assert.Equal("4:12", view.Remaining);
    }

    [Fact]
    public void ProgressView_VerifyDetailSelectsTheVerifyTitle()
    {
        var report = new ProgressReport("j", 2, 4, "Raw.Write", 0.5, 0.5, 1, 2, 0, null, ProgressView.VerifyDetail);

        Assert.Equal("3/4  Verifying written data", ProgressView.From(report, English).Title);
    }

    [Fact]
    public void ProgressView_UnknownStepFallsBackToItsKey()
    {
        var report = new ProgressReport("j", 0, 1, "Nope.Unknown", 0, 0, 0, 0, 0, null, null);

        Assert.Equal("Step.Nope.Unknown", ProgressView.From(report, English).Title);
    }

    [Fact]
    public void WriteChecks_ReportsTheFirstProblemInOrder()
    {
        var small = Stick() with { SizeBytes = 1L << 30 };

        Assert.Equal("Choose an image first.", WriteChecks.FirstProblem(null, null, [Stick()], English));
        Assert.Equal("Choose at least one drive.", WriteChecks.FirstProblem("a.iso", 1, [], English));
        Assert.Equal("The image (4 GB) does not fit on this drive (1 GB).", WriteChecks.FirstProblem("a.iso", 4L << 30, [small], English));
        Assert.Null(WriteChecks.FirstProblem("a.iso", 1L << 20, [small], English));
        Assert.Null(WriteChecks.FirstProblem("a.iso", null, [small], English));
    }
}
