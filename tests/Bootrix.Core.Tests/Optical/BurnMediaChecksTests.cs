// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Jobs;
using Bootrix.Core.Optical;
using Bootrix.Core.Optical.Images;
using Bootrix.Core.Optical.Jobs;
using Bootrix.Core.Tests.Optical.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Core.Tests.Optical;

/// <summary>What the burn job says about the disc before it writes anything.</summary>
public class BurnMediaChecksTests
{
    private static async Task<JobResult> Burn(FakeOpticalService service, OpticalDrive drive, int sectors = 100, BurnOptions? options = null)
    {
        var data = OpticalTestData.DiscImage(sectors);
        var job = new BurnImageJob(service, NullLogger<BurnImageJob>.Instance)
            .Create(new BurnImageRequest
            {
                Drives = [drive],
                Image = new DiscImageSource("x.iso", DiscImageKind.Iso, data.Length, () => new MemoryStream(data)),
                Options = options ?? new BurnOptions(),
            });
        return await new JobRunner(NullLogger<JobRunner>.Instance).RunAsync(job);
    }

    private static ErrorCode CodeOf(JobResult result) => Assert.IsType<BootrixException>(result.Error).Code;

    private static OpticalMedia Disc(OpticalMediaType type, OpticalMediaState state, long free = 2_295_104, long total = 2_295_104, bool blankHint = false) => new()
    {
        Type = type,
        State = state,
        IsSupported = true,
        FreeSectors = free,
        TotalSectors = total,
        HeuristicallyBlank = blankHint,
    };

    [Fact]
    public async Task EmptyTrayIsReported()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D", OpticalMedia.None);

        var result = await Burn(service, drive);

        Assert.Equal(ErrorCode.MediaNotSupported, CodeOf(result));
        Assert.Equal("Burn.CheckMedia", result.FailedStep);
        Assert.Empty(service.Burns);
    }

    [Fact]
    public async Task DriveThatCannotWriteIsRefused()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D", capabilities: OpticalCapabilities.None);

        Assert.Equal(ErrorCode.NoRecorder, CodeOf(await Burn(service, drive)));
    }

    [Fact]
    public async Task DiscTypeTheDriveCannotWriteIsRefused()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D", Disc(OpticalMediaType.BdR, OpticalMediaState.Blank), OpticalCapabilities.DvdPlusR);

        Assert.Equal(ErrorCode.MediaNotSupported, CodeOf(await Burn(service, drive)));
    }

    [Fact]
    public async Task ReadOnlyDiscIsRefused()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D", Disc(OpticalMediaType.DvdRom, OpticalMediaState.Finalized));

        Assert.Equal(ErrorCode.MediaNotSupported, CodeOf(await Burn(service, drive)));
    }

    [Fact]
    public async Task WriteProtectedDiscIsRefused()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D", Disc(OpticalMediaType.DvdPlusR, OpticalMediaState.Blank | OpticalMediaState.WriteProtected));

        Assert.Equal(ErrorCode.MediaNotSupported, CodeOf(await Burn(service, drive)));
    }

    [Theory]
    [InlineData(OpticalMediaState.Appendable | OpticalMediaState.NonEmptySession)]
    [InlineData(OpticalMediaState.Finalized | OpticalMediaState.NonEmptySession)]
    public async Task WrittenWriteOnceDiscIsNotBlank(OpticalMediaState state)
    {
        // An ISO burned after the data already on the disc would have every internal address off by the session start.
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D", Disc(OpticalMediaType.DvdMinusR, state, free: 1_000_000));

        Assert.Equal(ErrorCode.MediaNotBlank, CodeOf(await Burn(service, drive)));
    }

    [Fact]
    public async Task WrittenRewritableDiscNeedsAnEraseOrPermissionToOverwrite()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D", Disc(OpticalMediaType.CdRw, OpticalMediaState.NonEmptySession, free: 0, total: 359_848));

        Assert.Equal(ErrorCode.MediaNotBlank, CodeOf(await Burn(service, drive)));
    }

    [Fact]
    public async Task OverwritingARewritableDiscCountsTheWholeDisc()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D", Disc(OpticalMediaType.CdRw, OpticalMediaState.NonEmptySession, free: 0, total: 359_848));

        var result = await Burn(service, drive, options: new BurnOptions { ForceOverwrite = true });

        Assert.True(result.Succeeded, result.Error?.ToString());
    }

    [Fact]
    public async Task OverwritableDiscThatLooksEmptyIsAccepted()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive(
            "D",
            Disc(OpticalMediaType.DvdPlusRw, OpticalMediaState.OverwriteOnly, blankHint: true),
            OpticalCapabilities.DvdPlusRw);

        Assert.True((await Burn(service, drive)).Succeeded);
    }

    [Fact]
    public async Task ImageLargerThanTheDiscIsRejectedWithBothSizes()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D", Disc(OpticalMediaType.CdR, OpticalMediaState.Blank, free: 90, total: 90), OpticalCapabilities.CdR);

        var result = await Burn(service, drive, sectors: 100);

        var ex = Assert.IsType<BootrixException>(result.Error);
        Assert.Equal(ErrorCode.DeviceTooSmall, ex.Code);
        Assert.Equal(["200 KB", "180 KB"], ex.Arguments.Select(a => a?.ToString()));
        Assert.Empty(service.Burns);
    }

    [Fact]
    public async Task ImageThatExactlyFillsTheDiscIsFine()
    {
        var service = new FakeOpticalService();
        var drive = service.AddDrive("D", Disc(OpticalMediaType.CdR, OpticalMediaState.Blank, free: 100, total: 100), OpticalCapabilities.CdR);

        Assert.True((await Burn(service, drive, sectors: 100)).Succeeded);
    }

    [Fact]
    public async Task EveryDriveIsCheckedBeforeAnyBurnStarts()
    {
        var service = new FakeOpticalService();
        var good = service.AddDrive("D");
        var empty = service.AddDrive("E", OpticalMedia.None);
        var data = OpticalTestData.DiscImage(50);
        var job = new BurnImageJob(service, NullLogger<BurnImageJob>.Instance).Create(new BurnImageRequest
        {
            Drives = [good, empty],
            Image = new DiscImageSource("x.iso", DiscImageKind.Iso, data.Length, () => new MemoryStream(data)),
        });

        var result = await new JobRunner(NullLogger<JobRunner>.Instance).RunAsync(job);

        Assert.Equal(ErrorCode.MediaNotSupported, CodeOf(result));
        Assert.Empty(service.Burns);
    }
}
