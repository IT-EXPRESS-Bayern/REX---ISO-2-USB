// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Jobs;
using Bootrix.Core.Optical;
using Bootrix.Core.Optical.Jobs;
using Bootrix.Core.Tests.Optical.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Core.Tests.Optical;

public class EraseDiscJobTests
{
    private static EraseDiscJob Job(FakeOpticalService service) => new(service, NullLogger<EraseDiscJob>.Instance);

    private static OpticalMedia WrittenRewritable(OpticalMediaType type = OpticalMediaType.CdRw) => new()
    {
        Type = type,
        State = OpticalMediaState.NonEmptySession | OpticalMediaState.EraseRequired,
        IsSupported = true,
        TotalSectors = 359_848,
    };

    [Fact]
    public async Task QuickEraseMakesTheDiscBlank()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D", WrittenRewritable());
        var harness = new JobHarness();

        var result = await harness.RunAsync(Job(service).Create(new EraseDiscRequest { Drive = drive }));

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal([(drive, EraseMode.Quick)], service.Erases);
        Assert.Equal(OpticalMediaCondition.Blank, service.Media["D"].Condition);
        Assert.Equal(["Erase.Check", "Erase.Run", "Erase.Finish"], harness.StepKeys);
    }

    [Fact]
    public async Task FullEraseIsPassedOn()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D", WrittenRewritable(OpticalMediaType.DvdMinusRw), OpticalCapabilities.DvdMinusRw);

        await new JobHarness().RunAsync(Job(service).Create(new EraseDiscRequest { Drive = drive, Mode = EraseMode.Full }));

        Assert.Equal(EraseMode.Full, Assert.Single(service.Erases).Mode);
    }

    [Fact]
    public async Task ProgressFollowsTheDrivesEstimate()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D", WrittenRewritable());
        var harness = new JobHarness();

        await harness.RunAsync(Job(service).Create(new EraseDiscRequest { Drive = drive }));

        var erase = harness.Progress.Where(p => p.StepKey == "Erase.Run").Select(p => p.StepFraction).ToList();
        Assert.Contains(0.25, erase);
        Assert.Equal(1.0, harness.Progress[^1].OverallFraction, 6);
    }

    [Fact]
    public async Task WriteOnceDiscCannotBeErased()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D", new OpticalMedia { Type = OpticalMediaType.CdR, State = OpticalMediaState.Appendable, IsSupported = true, TotalSectors = 359_848 });

        var result = await new JobHarness().RunAsync(Job(service).Create(new EraseDiscRequest { Drive = drive }));

        Assert.Equal("Erase.Check", result.FailedStep);
        Assert.Equal(ErrorCode.MediaNotSupported, Assert.IsType<BootrixException>(result.Error).Code);
        Assert.Empty(service.Erases);
    }

    [Fact]
    public async Task EmptyTrayIsReported()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D", OpticalMedia.None);

        var result = await new JobHarness().RunAsync(Job(service).Create(new EraseDiscRequest { Drive = drive }));

        Assert.Equal(ErrorCode.MediaNotSupported, Assert.IsType<BootrixException>(result.Error).Code);
    }

    [Fact]
    public async Task ReadOnlyDriveIsReported()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D", WrittenRewritable(), OpticalCapabilities.None);

        var result = await new JobHarness().RunAsync(Job(service).Create(new EraseDiscRequest { Drive = drive }));

        Assert.Equal(ErrorCode.NoRecorder, Assert.IsType<BootrixException>(result.Error).Code);
    }

    [Fact]
    public async Task DriveThatCannotWriteThisDiscTypeIsReported()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D", WrittenRewritable(OpticalMediaType.BdRe), OpticalCapabilities.DvdPlusRw);

        var result = await new JobHarness().RunAsync(Job(service).Create(new EraseDiscRequest { Drive = drive }));

        Assert.Equal(ErrorCode.MediaNotSupported, Assert.IsType<BootrixException>(result.Error).Code);
    }

    [Fact]
    public async Task EjectsWhenAsked()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D", WrittenRewritable());

        await new JobHarness().RunAsync(Job(service).Create(new EraseDiscRequest { Drive = drive, EjectWhenDone = true }));

        Assert.Equal(["D"], service.Ejected);
    }
}
