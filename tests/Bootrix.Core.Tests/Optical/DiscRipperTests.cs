// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using Bootrix.Core.Errors;
using Bootrix.Core.Optical.Reading;
using Bootrix.Core.Tests.Optical.Support;

namespace Bootrix.Core.Tests.Optical;

public class DiscRipperTests
{
    private const int Sector = 2048;

    private static readonly RipOptions Fast = new() { RetryDelay = TimeSpan.Zero, ChunkSectors = 64, ChunkRetries = 3, SectorRetries = 2 };

    private static string Sha(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    private static byte[] ZeroSectors(byte[] image, params int[] sectors)
    {
        var copy = (byte[])image.Clone();
        foreach (var s in sectors)
        {
            copy.AsSpan(s * Sector, Sector).Clear();
        }

        return copy;
    }

    private static async Task<(RipReport Report, byte[] Data)> Rip(FakeSectorReader reader, RipOptions? options = null, IRipCheckpointStore? store = null)
    {
        var destination = new MemoryStream();
        var report = await new DiscRipper().RipAsync(reader, destination, options ?? Fast, store);
        return (report, destination.ToArray());
    }

    [Fact]
    public async Task CleanDiscIsCopiedExactly()
    {
        var image = OpticalTestData.DiscImage(600);

        var (report, data) = await Rip(new FakeSectorReader(image));

        Assert.Equal(image, data);
        Assert.Equal(600, report.SectorCount);
        Assert.True(report.IsComplete);
        Assert.Equal(Sha(image), report.Sha256);
        Assert.Equal(0, report.ResumedFromSector);
        Assert.Equal(600L * Sector, report.Bytes);
    }

    [Fact]
    public async Task DiscSmallerThanOneChunkWorks()
    {
        var image = OpticalTestData.DiscImage(10);

        var (report, data) = await Rip(new FakeSectorReader(image));

        Assert.Equal(image, data);
        Assert.Equal(Sha(image), report.Sha256);
    }

    [Fact]
    public async Task LastChunkMayBeShort()
    {
        var image = OpticalTestData.DiscImage(64 * 3 + 7);

        var (report, data) = await Rip(new FakeSectorReader(image));

        Assert.Equal(image, data);
        Assert.Equal(64 * 3 + 7, report.SectorCount);
    }

    [Fact]
    public async Task ReadsGoOutInChunks()
    {
        var reader = new FakeSectorReader(OpticalTestData.DiscImage(600));
        var ripper = new DiscRipper();
        var plan = await ripper.PlanAsync(reader, Fast);
        reader.Requests.Clear();

        await ripper.RipAsync(reader, new MemoryStream(), plan, Fast);

        Assert.Equal(10, reader.Requests.Count);
        Assert.All(reader.Requests.Take(9), r => Assert.Equal(64, r.Count));
        Assert.Equal((576L, 24), reader.Requests[9]);
    }

    [Fact]
    public async Task ShortReadsAreCompleted()
    {
        var image = OpticalTestData.DiscImage(300);
        var reader = new FakeSectorReader(image) { MaxSectorsPerRead = 10 };

        var (report, data) = await Rip(reader);

        Assert.Equal(image, data);
        Assert.True(report.IsComplete);
    }

    [Fact]
    public async Task FailureThatGoesAwayIsRetriedAsAWhole()
    {
        var image = OpticalTestData.DiscImage(600);
        var reader = new FakeSectorReader(image);
        reader.FailTransiently(100, 2);

        var (report, data) = await Rip(reader);

        Assert.Equal(image, data);
        Assert.True(report.IsComplete);
        Assert.DoesNotContain((100L, 1), reader.Requests);
    }

    [Fact]
    public async Task StubbornSectorIsReadOnItsOwnAfterTheChunkRetriesRunOut()
    {
        var image = OpticalTestData.DiscImage(600);
        var reader = new FakeSectorReader(image);

        // one failure in the first request, one in the next, three chunk retries, then the first single read fails as well
        reader.FailTransiently(100, 6);

        var (report, data) = await Rip(reader);

        Assert.Equal(image, data);
        Assert.True(report.IsComplete);
        Assert.Equal(2, reader.Requests.Count(r => r == (100L, 1)));
    }

    [Fact]
    public async Task UnreadableSectorsAreZeroFilledAndListed()
    {
        var image = OpticalTestData.DiscImage(600);
        var reader = new FakeSectorReader(image);
        reader.PermanentlyBad.UnionWith([300, 301, 450]);

        var (report, data) = await Rip(reader);

        var expected = ZeroSectors(image, 300, 301, 450);
        Assert.Equal(expected, data);
        Assert.False(report.IsComplete);
        Assert.Equal([new SectorRange(300, 2), new SectorRange(450, 1)], report.BadSectors.Ranges);
        Assert.Equal(3, report.BadSectors.Count);
        Assert.Equal(Sha(expected), report.Sha256);
    }

    [Fact]
    public async Task EachBadSectorGetsTheConfiguredNumberOfAttempts()
    {
        var reader = new FakeSectorReader(OpticalTestData.DiscImage(200));
        reader.PermanentlyBad.Add(150);

        await Rip(reader, Fast with { SectorRetries = 4 });

        // one attempt plus four retries
        Assert.Equal(5, reader.Requests.Count(r => r == (150L, 1)));
    }

    [Fact]
    public async Task SectorsBeforeABadOneAreKeptFromTheSameRequest()
    {
        var image = OpticalTestData.DiscImage(200);
        var reader = new FakeSectorReader(image);
        reader.PermanentlyBad.Add(40);
        var ripper = new DiscRipper();
        var plan = await ripper.PlanAsync(reader, Fast);
        reader.Requests.Clear();
        var destination = new MemoryStream();

        var report = await ripper.RipAsync(reader, destination, plan, Fast);

        Assert.Equal(ZeroSectors(image, 40), destination.ToArray());
        Assert.Equal(1, report.BadSectors.Count);
        // the first request delivered 40 good sectors; the next one starts at the bad sector instead of reading them again
        Assert.Equal((0L, 64), reader.Requests[0]);
        Assert.Equal((40L, 24), reader.Requests[1]);
    }

    [Fact]
    public async Task ReSeekReadsAFarSectorBetweenRetries()
    {
        var reader = new FakeSectorReader(OpticalTestData.DiscImage(200));
        reader.PermanentlyBad.Add(10);

        await Rip(reader);

        // the bad sector is in the first half, so the drive is sent to the end of the disc
        Assert.Contains((199L, 1), reader.Requests);
    }

    [Fact]
    public async Task ReadSpeedIsLoweredAfterTheFirstError()
    {
        var reader = new FakeSectorReader(OpticalTestData.DiscImage(200)) { SupportsSpeedControl = true };
        reader.FailTransiently(120, 2);

        var (report, _) = await Rip(reader, Fast with { ErrorReadSpeedKilobytesPerSecond = 1400 });

        Assert.Equal(1400, reader.RequestedSpeed);
        Assert.Contains(RipNote.SpeedReduced, report.Notes);
    }

    [Fact]
    public async Task NoteIsOmittedWhenTheDriveRefusesToSlowDown()
    {
        var reader = new FakeSectorReader(OpticalTestData.DiscImage(200)) { SupportsSpeedControl = false };
        reader.FailTransiently(120, 2);

        var (report, _) = await Rip(reader, Fast with { ErrorReadSpeedKilobytesPerSecond = 1400 });

        Assert.Null(reader.RequestedSpeed);
        Assert.DoesNotContain(RipNote.SpeedReduced, report.Notes);
    }

    [Fact]
    public async Task TooManyUnreadableSectorsStopTheRipAndKeepAResumePoint()
    {
        var reader = new FakeSectorReader(OpticalTestData.DiscImage(600));
        for (var lba = 100; lba < 200; lba++)
        {
            reader.PermanentlyBad.Add(lba);
        }

        var store = new MemoryCheckpointStore();
        var ex = await Assert.ThrowsAsync<BootrixException>(() => Rip(reader, Fast with { MaxBadSectors = 10 }, store));

        Assert.Equal(ErrorCode.ReadError, ex.Code);
        Assert.Equal(100L, ex.Arguments[0]);
        Assert.Equal(64, store.Current!.NextSector);
    }

    [Fact]
    public async Task AudioDiscIsRefused()
    {
        var reader = new FakeSectorReader(OpticalTestData.DiscImage(100))
        {
            Toc = TocParser.ParseFull(OpticalTestData.FullToc((1, [(1, false, 0), (2, false, 50)], 100, 0))),
        };

        var ex = await Assert.ThrowsAsync<BootrixException>(() => Rip(reader));

        Assert.Equal(ErrorCode.AudioDiscNotSupported, ex.Code);
    }

    [Fact]
    public async Task MixedModeDiscIsRefused()
    {
        var reader = new FakeSectorReader(OpticalTestData.DiscImage(100))
        {
            Toc = TocParser.ParseFull(OpticalTestData.FullToc((1, [(1, true, 0), (2, false, 50)], 100, 0))),
        };

        var ex = await Assert.ThrowsAsync<BootrixException>(() => Rip(reader));

        Assert.Equal(ErrorCode.MixedModeDiscNotSupported, ex.Code);
    }

    [Fact]
    public async Task EnhancedCdIsRefusedAsMixed()
    {
        var reader = new FakeSectorReader(OpticalTestData.DiscImage(100))
        {
            Toc = TocParser.ParseFull(OpticalTestData.FullToc(
                (1, [(1, false, 0)], 40, 0),
                (2, [(2, true, 50)], 100, 0x20))),
        };

        var ex = await Assert.ThrowsAsync<BootrixException>(() => Rip(reader));

        Assert.Equal(ErrorCode.MixedModeDiscNotSupported, ex.Code);
    }

    [Fact]
    public async Task MultiSessionDataDiscIsCopiedWithAHint()
    {
        var image = OpticalTestData.DiscImage(100);
        var reader = new FakeSectorReader(image)
        {
            Toc = TocParser.ParseFull(OpticalTestData.FullToc(
                (1, [(1, true, 0)], 40, 0),
                (2, [(2, true, 50)], 100, 0))),
        };

        var (report, data) = await Rip(reader);

        Assert.Equal(image, data);
        Assert.Contains(RipNote.MultiSession, report.Notes);
        Assert.NotNull(report.Toc);
        Assert.True(report.Toc.IsMultiSession);
    }

    [Fact]
    public async Task DiscWithoutTocIsTreatedAsData()
    {
        var (report, _) = await Rip(new FakeSectorReader(OpticalTestData.DiscImage(100)) { Toc = null });

        Assert.Null(report.Toc);
        Assert.DoesNotContain(RipNote.MultiSession, report.Notes);
    }

    [Theory]
    [InlineData(ProtectionSystem.Css)]
    [InlineData(ProtectionSystem.Cprm)]
    [InlineData(ProtectionSystem.Other)]
    public async Task CopyrightStructureOfAProtectedDvdRefusesTheRip(ProtectionSystem system)
    {
        var reader = new FakeSectorReader(OpticalTestData.DiscImage(100)) { Copyright = new DiscCopyrightInfo(system, 0) };

        var ex = await Assert.ThrowsAsync<BootrixException>(() => Rip(reader));

        Assert.Equal(ErrorCode.CopyProtected, ex.Code);
        Assert.Equal(system.ToString(), ex.Arguments[0]);
    }

    [Fact]
    public async Task UnprotectedDvdStructureDoesNotGetInTheWay()
    {
        var reader = new FakeSectorReader(OpticalTestData.DiscImage(100)) { Copyright = new DiscCopyrightInfo(ProtectionSystem.None, 0xFF) };

        var (report, _) = await Rip(reader);

        Assert.True(report.IsComplete);
    }

    [Fact]
    public async Task ScrambledSectorsStopTheRipImmediately()
    {
        var reader = new FakeSectorReader(OpticalTestData.DiscImage(600));
        reader.PermanentlyBad.Add(200);
        reader.FailureStatus[200] = SectorReadStatus.ProtectedContent;

        var ex = await Assert.ThrowsAsync<BootrixException>(() => Rip(reader));

        Assert.Equal(ErrorCode.CopyProtected, ex.Code);
        // no retries, no single-sector mode
        Assert.DoesNotContain((200L, 1), reader.Requests);
    }

    [Fact]
    public async Task ImageIsTrimmedToTheFileSystemWhenAsked()
    {
        // a rewritable disc formatted to 1000 sectors that holds a 400-sector file system
        var image = OpticalTestData.DiscImage(1000, volumeSectors: 400);

        var (report, data) = await Rip(new FakeSectorReader(image), Fast with { TrimToFileSystem = true });

        Assert.Equal(400, report.SectorCount);
        Assert.Equal(image[..(400 * Sector)], data);
        Assert.Contains(RipNote.TrimmedToFileSystem, report.Notes);
        Assert.Equal(Sha(image[..(400 * Sector)]), report.Sha256);
    }

    [Fact]
    public async Task ImageKeepsTheFullCapacityByDefault()
    {
        var image = OpticalTestData.DiscImage(1000, volumeSectors: 400);

        var (report, data) = await Rip(new FakeSectorReader(image));

        Assert.Equal(1000, report.SectorCount);
        Assert.Equal(image.Length, data.Length);
        Assert.DoesNotContain(RipNote.TrimmedToFileSystem, report.Notes);
    }

    [Fact]
    public async Task FileSystemLargerThanTheDiscDoesNotShrinkTheImage()
    {
        var image = OpticalTestData.DiscImage(300, volumeSectors: 500);

        var (report, _) = await Rip(new FakeSectorReader(image), Fast with { TrimToFileSystem = true });

        Assert.Equal(300, report.SectorCount);
    }

    [Fact]
    public async Task DriveThatReportsNoCapacityFallsBackToTheFileSystem()
    {
        var image = OpticalTestData.DiscImage(300);

        var (report, data) = await Rip(new FakeSectorReader(image, sectorCount: 0));

        Assert.Equal(300, report.SectorCount);
        Assert.Equal(image, data);
        Assert.Contains(RipNote.CapacityFromFileSystem, report.Notes);
    }

    [Fact]
    public async Task NoCapacityAndNoFileSystemMeansThereIsNothingToCopy()
    {
        var image = new byte[300 * Sector];

        var ex = await Assert.ThrowsAsync<BootrixException>(() => Rip(new FakeSectorReader(image, sectorCount: 0)));

        Assert.Equal(ErrorCode.DeviceNotFound, ex.Code);
    }

    [Fact]
    public async Task CancelledRipLeavesACheckpointAtAChunkBoundary()
    {
        var image = OpticalTestData.DiscImage(600);
        var reader = new FakeSectorReader(image);
        using var cts = new CancellationTokenSource();
        reader.BeforeRead = (lba, count) =>
        {
            if (lba >= 256 && count == 64)
            {
                cts.Cancel();
            }
        };
        var store = new MemoryCheckpointStore();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new DiscRipper().RipAsync(reader, new MemoryStream(), Fast, store, null, cts.Token));

        // the chunk that was being read when the cancellation arrived is finished and counted
        Assert.NotNull(store.Current);
        Assert.Equal(320, store.Current.NextSector);
        Assert.Equal(600, store.Current.SectorCount);
    }

    [Fact]
    public async Task InterruptedRipContinuesWhereItStopped()
    {
        var image = OpticalTestData.DiscImage(600);
        var destination = new MemoryStream();
        var store = new MemoryCheckpointStore();

        using (var cts = new CancellationTokenSource())
        {
            var first = new FakeSectorReader(image);
            first.BeforeRead = (lba, count) =>
            {
                if (lba >= 320 && count == 64)
                {
                    cts.Cancel();
                }
            };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => new DiscRipper().RipAsync(first, destination, Fast, store, null, cts.Token));
        }

        var second = new FakeSectorReader(image);
        var ripper = new DiscRipper();
        var plan = await ripper.PlanAsync(second, Fast);
        second.Requests.Clear();
        var report = await ripper.RipAsync(second, destination, plan, Fast, store);

        Assert.Equal(384, report.ResumedFromSector);
        Assert.Equal(image, destination.ToArray());
        Assert.Equal(Sha(image), report.Sha256);
        Assert.True(report.IsComplete);
        Assert.Null(store.Current);

        // only the probe sectors lie before the resume point
        Assert.True(second.Requests.Where(r => r.Lba < 384).All(r => r.Count == 1));
        Assert.True(second.Requests.Count(r => r.Lba < 384) <= 3);
    }

    [Fact]
    public async Task ResumeKeepsTheBadSectorsFoundBeforeTheInterruption()
    {
        var image = OpticalTestData.DiscImage(600);
        var destination = new MemoryStream();
        var store = new MemoryCheckpointStore();

        using (var cts = new CancellationTokenSource())
        {
            var first = new FakeSectorReader(image);
            first.PermanentlyBad.Add(100);
            first.BeforeRead = (lba, count) =>
            {
                if (lba >= 320 && count == 64)
                {
                    cts.Cancel();
                }
            };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => new DiscRipper().RipAsync(first, destination, Fast, store, null, cts.Token));
        }

        var second = new FakeSectorReader(image);
        second.PermanentlyBad.Add(100);
        var report = await new DiscRipper().RipAsync(second, destination, Fast, store);

        var expected = ZeroSectors(image, 100);
        Assert.Equal(expected, destination.ToArray());
        Assert.Equal(Sha(expected), report.Sha256);
        Assert.Equal([new SectorRange(100, 1)], report.BadSectors.Ranges);
    }

    [Fact]
    public async Task DifferentDiscInTheDriveStartsOver()
    {
        var firstImage = OpticalTestData.DiscImage(600, seed: 1);
        var secondImage = OpticalTestData.DiscImage(600, seed: 2);
        var destination = new MemoryStream();
        var store = new MemoryCheckpointStore();

        using (var cts = new CancellationTokenSource())
        {
            var first = new FakeSectorReader(firstImage);
            first.BeforeRead = (lba, count) =>
            {
                if (lba >= 320 && count == 64)
                {
                    cts.Cancel();
                }
            };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => new DiscRipper().RipAsync(first, destination, Fast, store, null, cts.Token));
        }

        var report = await new DiscRipper().RipAsync(new FakeSectorReader(secondImage), destination, Fast, store);

        Assert.Equal(0, report.ResumedFromSector);
        Assert.Contains(RipNote.CheckpointDiscarded, report.Notes);
        Assert.Equal(secondImage, destination.ToArray());
        Assert.Equal(Sha(secondImage), report.Sha256);
    }

    [Fact]
    public async Task CheckpointOfADifferentSizeIsDiscarded()
    {
        var image = OpticalTestData.DiscImage(600);
        var destination = new MemoryStream(image);
        var store = new MemoryCheckpointStore { Current = new RipCheckpoint { SectorCount = 700, NextSector = 300 } };

        var report = await new DiscRipper().RipAsync(new FakeSectorReader(image), destination, Fast, store);

        Assert.Equal(0, report.ResumedFromSector);
        Assert.Contains(RipNote.CheckpointDiscarded, report.Notes);
    }

    [Fact]
    public async Task CheckpointBeyondTheFileIsDiscarded()
    {
        var image = OpticalTestData.DiscImage(600);
        var destination = new MemoryStream();
        destination.Write(image, 0, 100 * Sector);
        var store = new MemoryCheckpointStore { Current = new RipCheckpoint { SectorCount = 600, NextSector = 300 } };

        var report = await new DiscRipper().RipAsync(new FakeSectorReader(image), destination, Fast, store);

        Assert.Contains(RipNote.CheckpointDiscarded, report.Notes);
        Assert.Equal(image, destination.ToArray());
    }

    [Fact]
    public async Task ResumeCanBeSwitchedOff()
    {
        var image = OpticalTestData.DiscImage(600);
        var destination = new MemoryStream(image);
        var store = new MemoryCheckpointStore { Current = new RipCheckpoint { SectorCount = 600, NextSector = 300 } };

        var report = await new DiscRipper().RipAsync(new FakeSectorReader(image), destination, Fast with { Resume = false }, store);

        Assert.Equal(0, report.ResumedFromSector);
        Assert.DoesNotContain(RipNote.CheckpointDiscarded, report.Notes);
    }

    [Fact]
    public async Task LosingTheDriveKeepsTheProgress()
    {
        var reader = new FakeSectorReader(OpticalTestData.DiscImage(600));
        reader.BeforeRead = (lba, count) =>
        {
            if (lba >= 192 && count == 64)
            {
                throw new BootrixException(ErrorCode.DeviceRemoved, "tray opened");
            }
        };
        var store = new MemoryCheckpointStore();

        var ex = await Assert.ThrowsAsync<BootrixException>(() => Rip(reader, Fast, store));

        Assert.Equal(ErrorCode.DeviceRemoved, ex.Code);
        Assert.Equal(192, store.Current!.NextSector);
    }

    [Fact]
    public async Task ProgressEndsAtTheTotalAndNeverGoesBack()
    {
        var reader = new FakeSectorReader(OpticalTestData.DiscImage(2000));
        reader.PermanentlyBad.Add(500);
        var reports = new List<RipProgress>();

        await new DiscRipper().RipAsync(reader, new MemoryStream(), Fast, null, new SynchronousProgress<RipProgress>(reports.Add));

        Assert.NotEmpty(reports);
        var last = reports[^1];
        Assert.Equal(RipPhase.Finishing, last.Phase);
        Assert.Equal(2000, last.SectorsDone);
        Assert.Equal(2000, last.SectorsTotal);
        Assert.Equal(1, last.BadSectors);
        Assert.Equal(1.0, last.Fraction);
        for (var i = 1; i < reports.Count; i++)
        {
            Assert.True(reports[i].SectorsDone >= reports[i - 1].SectorsDone);
        }
    }

    [Fact]
    public async Task CheckpointsAreWrittenWhileRipping()
    {
        var reader = new FakeSectorReader(OpticalTestData.DiscImage(600));
        var store = new MemoryCheckpointStore();

        await Rip(reader, Fast with { CheckpointInterval = TimeSpan.Zero }, store);

        Assert.True(store.Saves >= 5);
        Assert.Null(store.Current);
    }

    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
