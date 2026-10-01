// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using Bootrix.Core.Errors;
using Bootrix.Core.Jobs;
using Bootrix.Core.Optical;
using Bootrix.Core.Optical.Jobs;
using Bootrix.Core.Optical.Reading;
using Bootrix.Core.Tests.Optical.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Core.Tests.Optical;

public sealed class RipDiscJobTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bootrix-ripjob-" + Guid.NewGuid().ToString("N"));

    public RipDiscJobTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static RipDiscJob Job(FakeOpticalService service) => new(service, NullLogger<RipDiscJob>.Instance);

    private static OpticalMedia WrittenDisc(OpticalMediaType type = OpticalMediaType.DvdMinusR) => new()
    {
        Type = type,
        State = OpticalMediaState.Finalized | OpticalMediaState.NonEmptySession,
        IsSupported = true,
        TotalSectors = 2_295_104,
    };

    private static readonly RipOptions Fast = new() { RetryDelay = TimeSpan.Zero };

    [Fact]
    public async Task DiscBecomesAnIsoWithAHashFile()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D", WrittenDisc());
        var image = OpticalTestData.DiscImage(500);
        service.Discs["D"] = image;
        var path = Path.Combine(_dir, "backup.iso");
        var harness = new JobHarness(RipDiscJob.ReportKey);

        var result = await harness.RunAsync(Job(service).Create(new RipDiscRequest { Drive = drive, IsoPath = path, Options = Fast }));

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal(image, File.ReadAllBytes(path));
        var hash = Convert.ToHexStringLower(SHA256.HashData(image));
        Assert.Equal($"{hash} *backup.iso\n", File.ReadAllText(RipDiscJob.HashFilePath(path)));
        Assert.False(File.Exists(RipDiscJob.BadSectorFilePath(path)));
        Assert.True(Assert.IsType<RipReport>(harness.Values[RipDiscJob.ReportKey]).IsComplete);
        Assert.Equal(["Rip.Check", "Rip.Read", "Rip.Finish"], harness.StepKeys);
        Assert.Equal(1.0, harness.Progress[^1].OverallFraction, 6);
    }

    [Fact]
    public async Task UnreadableSectorsAreListedNextToTheImage()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D", WrittenDisc());
        var image = OpticalTestData.DiscImage(500);
        service.Discs["D"] = image;
        service.ReaderCreated = reader =>
        {
            reader.PermanentlyBad.UnionWith([200, 201, 202, 400]);
        };
        var path = Path.Combine(_dir, "scratched.iso");

        var result = await new JobHarness().RunAsync(Job(service).Create(new RipDiscRequest { Drive = drive, IsoPath = path, Options = Fast }));

        Assert.True(result.Succeeded, result.Error?.ToString());
        var lines = File.ReadAllLines(RipDiscJob.BadSectorFilePath(path));
        Assert.Equal(["200-202", "400"], lines.Where(l => !l.StartsWith('#')));
        Assert.Contains(lines, l => l.Contains("4 of 500 sectors"));
        var expected = (byte[])image.Clone();
        foreach (var lba in new[] { 200, 201, 202, 400 })
        {
            expected.AsSpan(lba * 2048, 2048).Clear();
        }

        Assert.Equal(expected, File.ReadAllBytes(path));
        Assert.StartsWith(Convert.ToHexStringLower(SHA256.HashData(expected)), File.ReadAllText(RipDiscJob.HashFilePath(path)));
    }

    [Fact]
    public async Task StaleBadSectorListIsRemovedAfterACleanRip()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D", WrittenDisc());
        service.Discs["D"] = OpticalTestData.DiscImage(100);
        var path = Path.Combine(_dir, "disc.iso");
        File.WriteAllText(RipDiscJob.BadSectorFilePath(path), "old");

        await new JobHarness().RunAsync(Job(service).Create(new RipDiscRequest { Drive = drive, IsoPath = path, Options = Fast }));

        Assert.False(File.Exists(RipDiscJob.BadSectorFilePath(path)));
    }

    [Fact]
    public async Task SidecarFilesCanBeSwitchedOff()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D", WrittenDisc());
        service.Discs["D"] = OpticalTestData.DiscImage(100);
        var path = Path.Combine(_dir, "disc.iso");

        await new JobHarness().RunAsync(Job(service).Create(new RipDiscRequest { Drive = drive, IsoPath = path, Options = Fast, WriteSidecarFiles = false }));

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(RipDiscJob.HashFilePath(path)));
    }

    [Theory]
    [InlineData(OpticalMediaType.DvdPlusRw, true)]
    [InlineData(OpticalMediaType.DvdRam, true)]
    [InlineData(OpticalMediaType.BdRe, true)]
    [InlineData(OpticalMediaType.DvdMinusR, false)]
    [InlineData(OpticalMediaType.CdR, false)]
    [InlineData(OpticalMediaType.DvdRom, false)]
    public async Task FormattedCapacityMediaAreTrimmedToTheFileSystem(OpticalMediaType type, bool trimmed)
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D", WrittenDisc(type));
        service.Discs["D"] = OpticalTestData.DiscImage(100);

        await new JobHarness().RunAsync(Job(service).Create(new RipDiscRequest { Drive = drive, IsoPath = Path.Combine(_dir, "x.iso"), Options = Fast }));

        Assert.Equal(trimmed, service.LastRipOptions!.TrimToFileSystem);
    }

    [Fact]
    public async Task ExplicitTrimIsKeptOnOtherMedia()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D", WrittenDisc(OpticalMediaType.DvdMinusR));
        service.Discs["D"] = OpticalTestData.DiscImage(100);

        await new JobHarness().RunAsync(Job(service).Create(new RipDiscRequest { Drive = drive, IsoPath = Path.Combine(_dir, "x.iso"), Options = Fast with { TrimToFileSystem = true } }));

        Assert.True(service.LastRipOptions!.TrimToFileSystem);
    }

    [Fact]
    public async Task EmptyTrayFailsInTheCheckStep()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D", OpticalMedia.None);

        var result = await new JobHarness().RunAsync(Job(service).Create(new RipDiscRequest { Drive = drive, IsoPath = Path.Combine(_dir, "x.iso"), Options = Fast }));

        Assert.Equal("Rip.Check", result.FailedStep);
        Assert.Equal(ErrorCode.DeviceNotFound, Assert.IsType<BootrixException>(result.Error).Code);
    }

    [Fact]
    public async Task ProtectedDiscIsRefusedAndLeavesNoImage()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D", WrittenDisc(OpticalMediaType.DvdRom));
        service.Discs["D"] = OpticalTestData.DiscImage(100);
        service.ReaderCreated = reader => reader.Copyright = new DiscCopyrightInfo(ProtectionSystem.Css, 0);
        var path = Path.Combine(_dir, "movie.iso");

        var result = await new JobHarness().RunAsync(Job(service).Create(new RipDiscRequest { Drive = drive, IsoPath = path, Options = Fast }));

        Assert.Equal(ErrorCode.CopyProtected, Assert.IsType<BootrixException>(result.Error).Code);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task AudioCdIsRefused()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D", WrittenDisc(OpticalMediaType.CdR));
        service.Discs["D"] = OpticalTestData.DiscImage(100);
        service.ReaderCreated = reader => reader.Toc = TocParser.ParseFull(OpticalTestData.FullToc((1, [(1, false, 0)], 100, 0)));

        var result = await new JobHarness().RunAsync(Job(service).Create(new RipDiscRequest { Drive = drive, IsoPath = Path.Combine(_dir, "x.iso"), Options = Fast }));

        Assert.Equal(ErrorCode.AudioDiscNotSupported, Assert.IsType<BootrixException>(result.Error).Code);
    }

    [Fact]
    public async Task CancelledJobContinuesWhenRunAgain()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D", WrittenDisc());
        var image = OpticalTestData.DiscImage(1000);
        service.Discs["D"] = image;
        var path = Path.Combine(_dir, "big.iso");
        var request = new RipDiscRequest { Drive = drive, IsoPath = path, Options = Fast };

        using (var cts = new CancellationTokenSource())
        {
            service.ReaderCreated = reader => reader.BeforeRead = (lba, count) =>
            {
                if (lba >= 512 && count == 64)
                {
                    cts.Cancel();
                }
            };
            var first = await new JobHarness().RunAsync(Job(service).Create(request), cts.Token);
            Assert.Equal(JobOutcome.Canceled, first.Outcome);
        }

        Assert.True(File.Exists(path + ".btxrip"));

        service.ReaderCreated = null;
        var harness = new JobHarness(RipDiscJob.ReportKey);
        var second = await harness.RunAsync(Job(service).Create(request));

        Assert.True(second.Succeeded, second.Error?.ToString());
        Assert.Equal(image, File.ReadAllBytes(path));
        Assert.True(Assert.IsType<RipReport>(harness.Values[RipDiscJob.ReportKey]).ResumedFromSector >= 512);
        Assert.False(File.Exists(path + ".btxrip"));
    }

    [Fact]
    public async Task EjectsAfterwardsWhenAsked()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D", WrittenDisc());
        service.Discs["D"] = OpticalTestData.DiscImage(100);

        await new JobHarness().RunAsync(Job(service).Create(new RipDiscRequest { Drive = drive, IsoPath = Path.Combine(_dir, "x.iso"), Options = Fast, EjectWhenDone = true }));

        Assert.Equal(["D"], service.Ejected);
    }
}
