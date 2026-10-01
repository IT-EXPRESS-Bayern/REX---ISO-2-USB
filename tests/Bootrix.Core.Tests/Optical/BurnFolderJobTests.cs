// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Jobs;
using Bootrix.Core.Optical;
using Bootrix.Core.Optical.Jobs;
using Bootrix.Core.Tests.Optical.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Core.Tests.Optical;

public sealed class BurnFolderJobTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bootrix-folderjob-" + Guid.NewGuid().ToString("N"));

    public BurnFolderJobTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static BurnFolderJob Job(FakeOpticalService service) => new(service, NullLogger<BurnFolderJob>.Instance);

    private string Source(params (string Path, int Size)[] files)
    {
        var root = Path.Combine(_dir, "Holiday Photos");
        Directory.CreateDirectory(root);
        foreach (var (path, size) in files)
        {
            var full = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, new byte[size]);
        }

        return root;
    }

    [Fact]
    public async Task FolderIsBuiltAndBurnedWithThePlannedFileSystems()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D");
        var root = Source(("a.jpg", 5000), ("sub/b.jpg", 70_000));
        var harness = new JobHarness(BurnFolderJob.PlansKey, BurnFolderJob.ReportKey);

        var result = await harness.RunAsync(Job(service).Create(new BurnFolderRequest { Drives = [drive], Folder = new FolderBurnRequest { SourceFolder = root } }));

        Assert.True(result.Succeeded, result.Error?.ToString());
        var (_, request, options) = Assert.Single(service.FolderBurns);
        Assert.Equal(DiscFileSystems.Iso9660 | DiscFileSystems.Joliet | DiscFileSystems.Udf, request.FileSystems);
        Assert.Equal(UdfRevision.Udf102, request.UdfRevision);
        Assert.Equal("Holiday Photos", options.VolumeLabel);
        Assert.Equal(["Folder.Scan", "Burn.CheckMedia", "Burn.Write", "Burn.Finish"], harness.StepKeys);
        Assert.True(Assert.IsType<BurnReport>(harness.Values[BurnFolderJob.ReportKey]).AllSucceeded);
    }

    [Fact]
    public async Task VolumeLabelFromTheOptionsIsCleanedUp()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D");
        var root = Source(("a.txt", 10));

        await new JobHarness().RunAsync(Job(service).Create(new BurnFolderRequest
        {
            Drives = [drive],
            Folder = new FolderBurnRequest { SourceFolder = root },
            Options = new BurnOptions { VolumeLabel = "Backup: 2024/10" },
        }));

        Assert.Equal("Backup_ 2024_10", Assert.Single(service.FolderBurns).Options.VolumeLabel);
    }

    [Fact]
    public async Task EachDriveGetsAPlanForItsOwnDisc()
    {
        var service = new FakeOpticalService();
        var dvd = service.AddDrive("D");
        var bd = service.AddDrive("E", new OpticalMedia
        {
            Type = OpticalMediaType.BdR,
            State = OpticalMediaState.Blank,
            IsSupported = true,
            FreeSectors = 12_000_000,
            TotalSectors = 12_000_000,
        }, OpticalCapabilities.BdR);
        var root = Source(("a.txt", 10));

        var result = await new JobHarness().RunAsync(Job(service).Create(new BurnFolderRequest { Drives = [dvd, bd], Folder = new FolderBurnRequest { SourceFolder = root } }));

        Assert.True(result.Succeeded, result.Error?.ToString());
        var byDrive = service.FolderBurns.ToDictionary(b => b.Drive.Id, b => b.Request);
        Assert.Equal(DiscFileSystems.Iso9660 | DiscFileSystems.Joliet | DiscFileSystems.Udf, byDrive["D"].FileSystems);
        Assert.Equal(DiscFileSystems.Udf, byDrive["E"].FileSystems);
        Assert.Equal(UdfRevision.Udf250, byDrive["E"].UdfRevision);
    }

    [Fact]
    public async Task WindowsSetupTreeWithAHugeInstallImageIsTurnedAwayBeforeBurning()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D", FakeOpticalService.BlankDvd(sectors: 30_000_000));
        var root = Source(("bootmgr", 10), ("sources/boot.wim", 10));
        // a sparse 4.5 GB install.wim: the length is what counts, nothing is written
        using (var wim = new FileStream(Path.Combine(root, "sources", "install.wim"), FileMode.Create))
        {
            wim.SetLength(4_500_000_000);
        }

        var result = await new JobHarness().RunAsync(Job(service).Create(new BurnFolderRequest { Drives = [drive], Folder = new FolderBurnRequest { SourceFolder = root } }));

        Assert.Equal(JobOutcome.Failed, result.Outcome);
        var ex = Assert.IsType<BootrixException>(result.Error);
        Assert.Equal(ErrorCode.DiscFileTooLarge, ex.Code);
        Assert.Equal("install.wim", ex.Arguments[0]);
        Assert.Empty(service.FolderBurns);
    }

    [Fact]
    public async Task FolderLargerThanTheDiscIsRejected()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D", FakeOpticalService.BlankDvd(sectors: 10));
        var root = Source(("big.bin", 100 * 2048));

        var result = await new JobHarness().RunAsync(Job(service).Create(new BurnFolderRequest { Drives = [drive], Folder = new FolderBurnRequest { SourceFolder = root } }));

        Assert.Equal(ErrorCode.DeviceTooSmall, Assert.IsType<BootrixException>(result.Error).Code);
    }

    [Fact]
    public async Task MissingFolderFailsInTheScanStep()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D");

        var result = await new JobHarness().RunAsync(Job(service).Create(new BurnFolderRequest
        {
            Drives = [drive],
            Folder = new FolderBurnRequest { SourceFolder = Path.Combine(_dir, "nope") },
        }));

        Assert.Equal("Folder.Scan", result.FailedStep);
        Assert.Equal(ErrorCode.ImageUnreadable, Assert.IsType<BootrixException>(result.Error).Code);
        Assert.Empty(service.Calls);
    }

    [Fact]
    public async Task FailureOnOneDriveIsReported()
    {
        var service = new FakeOpticalService();
        var drives = new[] { service.AddDrive("D"), service.AddDrive("E") };
        service.FailBurn("E", new BootrixException(ErrorCode.DeviceBusy, "locked") { Arguments = ["Explorer"] });
        var root = Source(("a.txt", 10));
        var harness = new JobHarness(BurnFolderJob.ReportKey);

        var result = await harness.RunAsync(Job(service).Create(new BurnFolderRequest { Drives = drives, Folder = new FolderBurnRequest { SourceFolder = root } }));

        Assert.Equal(ErrorCode.DeviceBusy, Assert.IsType<BootrixException>(result.Error).Code);
        var report = Assert.IsType<BurnReport>(harness.Values[BurnFolderJob.ReportKey]);
        Assert.Equal([true, false], report.Drives.Select(d => d.Succeeded));
    }

    [Fact]
    public void BootImageThatDoesNotExistIsCaughtWhenTheJobIsCreated()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D");

        var ex = Assert.Throws<BootrixException>(() => Job(service).Create(new BurnFolderRequest
        {
            Drives = [drive],
            Folder = new FolderBurnRequest
            {
                SourceFolder = _dir,
                BootEntries = [new DiscBootEntry(BootPlatform.Bios, Path.Combine(_dir, "etfsboot.com"))],
            },
        }));

        Assert.Equal(ErrorCode.ImageUnreadable, ex.Code);
    }

    [Fact]
    public async Task BootEntriesReachTheService()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D");
        var root = Source(("a.txt", 10));
        var bios = Path.Combine(_dir, "etfsboot.com");
        var efi = Path.Combine(_dir, "efisys.bin");
        File.WriteAllBytes(bios, new byte[2048]);
        File.WriteAllBytes(efi, new byte[2048]);
        var entries = new[] { new DiscBootEntry(BootPlatform.Bios, bios), new DiscBootEntry(BootPlatform.Efi, efi) };

        await new JobHarness().RunAsync(Job(service).Create(new BurnFolderRequest
        {
            Drives = [drive],
            Folder = new FolderBurnRequest { SourceFolder = root, BootEntries = entries },
        }));

        Assert.Equal(entries, Assert.Single(service.FolderBurns).Request.BootEntries);
    }

    [Fact]
    public void ReadBackNeedsAnImageFile()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D");

        var ex = Assert.Throws<BootrixException>(() => Job(service).Create(new BurnFolderRequest
        {
            Drives = [drive],
            Folder = new FolderBurnRequest { SourceFolder = _dir },
            Options = new BurnOptions { ReadBackSha256 = true },
        }));

        Assert.Equal(ErrorCode.InvalidSpec, ex.Code);
    }

    [Fact]
    public void NoDriveIsAnError()
    {
        var ex = Assert.Throws<BootrixException>(() => Job(new FakeOpticalService()).Create(new BurnFolderRequest
        {
            Drives = [],
            Folder = new FolderBurnRequest { SourceFolder = _dir },
        }));

        Assert.Equal(ErrorCode.NoRecorder, ex.Code);
    }
}
