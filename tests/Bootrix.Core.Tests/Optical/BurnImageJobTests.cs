// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using Bootrix.Core.Errors;
using Bootrix.Core.Jobs;
using Bootrix.Core.Optical;
using Bootrix.Core.Optical.Images;
using Bootrix.Core.Optical.Jobs;
using Bootrix.Core.Tests.Optical.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Core.Tests.Optical;

public class BurnImageJobTests
{
    private static JobRunner Runner => new(NullLogger<JobRunner>.Instance);

    private static BurnImageJob Job(FakeOpticalService service) =>
        new(service, NullLogger<BurnImageJob>.Instance) { ReadBackRetryDelay = TimeSpan.Zero, ReadBackAttempts = 5 };

    private static DiscImageSource Image(byte[] data, string name = "disc.iso") =>
        new(name, DiscImageKind.Iso, data.Length, () => new MemoryStream(data));

    private static byte[] Padded(byte[] data)
    {
        var padded = new byte[SectorMath.RoundUpToSector(data.Length)];
        data.CopyTo(padded, 0);
        return padded;
    }

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static async Task<(JobResult Result, List<ProgressReport> Progress)> Run(BurnImageJob job, BurnImageRequest request)
    {
        var reports = new List<ProgressReport>();
        var result = await Runner.RunAsync(job.Create(request), new DelegateProgressSink(reports.Add));
        return (result, reports);
    }

    [Fact]
    public async Task ImageIsBurnedToTheDisc()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D");
        var image = OpticalTestData.DiscImage(300);

        var (result, progress) = await Run(Job(service), new BurnImageRequest { Drives = [drive], Image = Image(image) });

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal(image, service.Discs["D"]);
        Assert.Equal(["Burn.CheckMedia", "Burn.Write", "Burn.Finish"], progress.Select(p => p.StepKey).Distinct());
        Assert.Equal(1.0, progress[^1].OverallFraction, 6);
        Assert.Empty(service.Ejected);
    }

    [Fact]
    public async Task OptionsReachTheService()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D");
        var options = new BurnOptions { WriteSpeedFactor = 8, Finalize = false, Verify = BurnVerifyLevel.Full };

        await Run(Job(service), new BurnImageRequest { Drives = [drive], Image = Image(OpticalTestData.DiscImage(50)), Options = options });

        Assert.Equal(options, Assert.Single(service.Burns).Options);
    }

    [Fact]
    public async Task ImageThatIsNotAWholeNumberOfSectorsIsPadded()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D");
        var image = new byte[5000];
        new Random(4).NextBytes(image);

        var (result, _) = await Run(Job(service), new BurnImageRequest { Drives = [drive], Image = Image(image) });

        Assert.True(result.Succeeded);
        Assert.Equal(Padded(image), service.Discs["D"]);
    }

    [Fact]
    public async Task SeveralDrivesBurnAtTheSameTime()
    {
        var service = new FakeOpticalService { BurnBarrier = new CountdownEvent(3) };
        var drives = new[] { service.AddDrive("D"), service.AddDrive("E"), service.AddDrive("F") };
        var image = OpticalTestData.DiscImage(400);

        var (result, progress) = await Run(Job(service), new BurnImageRequest { Drives = drives, Image = Image(image) });

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.True(service.BurnsStartedTogether, "the burns ran one after the other");
        Assert.All(drives, d => Assert.Equal(image, service.Discs[d.Id]));
        Assert.Equal(1.0, progress[^1].OverallFraction, 6);
        for (var i = 1; i < progress.Count; i++)
        {
            Assert.True(progress[i].OverallFraction >= progress[i - 1].OverallFraction - 1e-9);
        }
    }

    [Fact]
    public async Task OneFailingDriveDoesNotStopTheOthers()
    {
        var service = new FakeOpticalService();
        var drives = new[] { service.AddDrive("D"), service.AddDrive("E") };
        service.FailBurn("D", new BootrixException(ErrorCode.BurnFailed, "calibration") { Arguments = ["power calibration failed"] });
        var image = OpticalTestData.DiscImage(100);

        var job = Job(service).Create(new BurnImageRequest { Drives = drives, Image = Image(image) });
        var harness = new JobHarness(BurnImageJob.ReportKey);
        var result = await harness.RunAsync(job);

        Assert.Equal(JobOutcome.Failed, result.Outcome);
        Assert.Equal("Burn.Write", result.FailedStep);
        var ex = Assert.IsType<BootrixException>(result.Error);
        Assert.Equal(ErrorCode.BurnFailed, ex.Code);
        Assert.Equal(image, service.Discs["E"]);
        Assert.False(service.Discs.ContainsKey("D"));
        var report = Assert.IsType<BurnReport>(harness.Values[BurnImageJob.ReportKey]);
        Assert.False(report.AllSucceeded);
        Assert.Equal([false, true], report.Drives.Select(d => d.Succeeded));
    }

    [Fact]
    public async Task UnexpectedErrorsAreWrappedAsBurnFailures()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D");
        service.FailBurn("D", new InvalidOperationException("drive hung"));

        var (result, _) = await Run(Job(service), new BurnImageRequest { Drives = [drive], Image = Image(OpticalTestData.DiscImage(50)) });

        var ex = Assert.IsType<BootrixException>(result.Error);
        Assert.Equal(ErrorCode.BurnFailed, ex.Code);
        Assert.Equal("drive hung", ex.Arguments[0]);
    }

    [Fact]
    public async Task CancellingStopsEveryDrive()
    {
        var service = new FakeOpticalService();
        var drives = new[] { service.AddDrive("D"), service.AddDrive("E") };
        using var cts = new CancellationTokenSource();
        var job = Job(service).Create(new BurnImageRequest { Drives = drives, Image = Image(OpticalTestData.DiscImage(5000)) });

        var result = await Runner.RunAsync(job, new DelegateProgressSink(p =>
        {
            if (p.StepKey == "Burn.Write" && p.OverallFraction > 0.2)
            {
                cts.Cancel();
            }
        }), cts.Token);

        Assert.Equal(JobOutcome.Canceled, result.Outcome);
        Assert.Empty(service.Discs);
    }

    [Fact]
    public async Task ReadBackConfirmsTheDiscByHash()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D");
        var image = OpticalTestData.DiscImage(2500);
        var harness = new JobHarness(BurnImageJob.ReportKey);

        var job = Job(service).Create(new BurnImageRequest { Drives = [drive], Image = Image(image), Options = new BurnOptions { ReadBackSha256 = true } });
        var result = await harness.RunAsync(job);

        Assert.True(result.Succeeded, result.Error?.ToString());
        var report = Assert.IsType<BurnReport>(harness.Values[BurnImageJob.ReportKey]);
        Assert.Equal(Sha(image), report.SourceSha256);
        Assert.True(Assert.Single(report.Drives).ReadBackVerified);
        Assert.Equal(["Burn.CheckMedia", "Burn.HashSource", "Burn.Write", "Burn.ReadBack", "Burn.Finish"], harness.StepKeys);
    }

    [Fact]
    public async Task ReadBackDetectsAWrongSectorAndSaysWhere()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D");
        var image = OpticalTestData.DiscImage(2500);
        // a sector in the second megabyte reads back wrong
        service.ReaderCreated = _ => service.Discs["D"][600 * 2048 + 17] ^= 0xFF;

        var (result, _) = await Run(Job(service), new BurnImageRequest { Drives = [drive], Image = Image(image), Options = new BurnOptions { ReadBackSha256 = true } });

        Assert.Equal(JobOutcome.Failed, result.Outcome);
        Assert.Equal("Burn.ReadBack", result.FailedStep);
        var ex = Assert.IsType<BootrixException>(result.Error);
        Assert.Equal(ErrorCode.VerifyMismatch, ex.Code);
        Assert.Equal((long)ImageDigest.DefaultChunkSize, ex.Arguments[0]);
    }

    [Fact]
    public async Task ReadBackWaitsUntilWindowsHasMountedTheDisc()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D");
        service.NotReady("D", 3);

        var (result, _) = await Run(Job(service), new BurnImageRequest { Drives = [drive], Image = Image(OpticalTestData.DiscImage(100)), Options = new BurnOptions { ReadBackSha256 = true } });

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal(4, service.OpenAttempts);
    }

    [Fact]
    public async Task ReadBackGivesUpWhenTheDriveNeverComesBack()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D");
        service.NotReady("D", 100);

        var (result, _) = await Run(Job(service), new BurnImageRequest { Drives = [drive], Image = Image(OpticalTestData.DiscImage(100)), Options = new BurnOptions { ReadBackSha256 = true } });

        Assert.Equal(JobOutcome.Failed, result.Outcome);
        Assert.Equal(ErrorCode.DeviceNotFound, Assert.IsType<BootrixException>(result.Error).Code);
        Assert.Equal(5, service.OpenAttempts);
    }

    [Fact]
    public async Task EjectsWhenAsked()
    {
        var service = new FakeOpticalService();
        var drives = new[] { service.AddDrive("D"), service.AddDrive("E") };

        var (result, _) = await Run(Job(service), new BurnImageRequest { Drives = drives, Image = Image(OpticalTestData.DiscImage(50)), Options = new BurnOptions { EjectWhenDone = true } });

        Assert.True(result.Succeeded);
        Assert.Equal(["D", "E"], service.Ejected.Order());
    }

    [Fact]
    public async Task TrayThatWillNotOpenDoesNotFailTheJob()
    {
        var service = new FakeOpticalService { EjectFailure = new BootrixException(ErrorCode.DeviceBusy, "locked") };
        var drive = service.AddDrive("D");

        var (result, _) = await Run(Job(service), new BurnImageRequest { Drives = [drive], Image = Image(OpticalTestData.DiscImage(50)), Options = new BurnOptions { EjectWhenDone = true } });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void NoDriveIsAnError()
    {
        var ex = Assert.Throws<BootrixException>(() => Job(new FakeOpticalService()).Create(new BurnImageRequest { Drives = [], Image = Image(new byte[2048]) }));

        Assert.Equal(ErrorCode.NoRecorder, ex.Code);
    }

    [Fact]
    public void InvalidOptionsAreRejectedWhenTheJobIsCreated()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D");

        var ex = Assert.Throws<BootrixException>(() =>
            Job(service).Create(new BurnImageRequest { Drives = [drive], Image = Image(new byte[2048]), Options = new BurnOptions { SectorSize = 2352 } }));

        Assert.Equal(ErrorCode.InvalidSpec, ex.Code);
    }
}
