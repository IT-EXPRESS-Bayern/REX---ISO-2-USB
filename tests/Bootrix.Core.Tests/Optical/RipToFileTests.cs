// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Optical.Reading;
using Bootrix.Core.Tests.Optical.Support;

namespace Bootrix.Core.Tests.Optical;

public sealed class RipToFileTests : IDisposable
{
    private static readonly RipOptions Fast = new() { RetryDelay = TimeSpan.Zero };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bootrix-rip-" + Guid.NewGuid().ToString("N"));

    public RipToFileTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task ImageIsWrittenAndTheCheckpointRemoved()
    {
        var image = OpticalTestData.DiscImage(500);
        var path = Path.Combine(_dir, "disc.iso");

        var report = await new DiscRipper().RipToFileAsync(new FakeSectorReader(image), path, Fast);

        Assert.Equal(image, File.ReadAllBytes(path));
        Assert.True(report.IsComplete);
        Assert.False(File.Exists(path + ".btxrip"));
        Assert.False(File.Exists(path + ".btxrip.tmp"));
    }

    [Fact]
    public async Task ExistingLongerFileIsCutToTheImageLength()
    {
        var image = OpticalTestData.DiscImage(100);
        var path = Path.Combine(_dir, "disc.iso");
        File.WriteAllBytes(path, new byte[500 * 2048]);

        await new DiscRipper().RipToFileAsync(new FakeSectorReader(image), path, Fast);

        Assert.Equal(image, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task InterruptedRipOnDiskContinuesInTheNextRun()
    {
        var image = OpticalTestData.DiscImage(800);
        var path = Path.Combine(_dir, "disc.iso");
        var checkpointFile = path + ".btxrip";

        using (var cts = new CancellationTokenSource())
        {
            var first = new FakeSectorReader(image);
            first.BeforeRead = (lba, count) =>
            {
                if (lba >= 384 && count == 64)
                {
                    cts.Cancel();
                }
            };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => new DiscRipper().RipToFileAsync(first, path, Fast, null, cts.Token));
        }

        Assert.True(File.Exists(checkpointFile));
        var saved = new FileRipCheckpointStore(checkpointFile).Load();
        Assert.Equal(448, saved!.NextSector);

        var report = await new DiscRipper().RipToFileAsync(new FakeSectorReader(image), path, Fast);

        Assert.Equal(448, report.ResumedFromSector);
        Assert.Equal(image, File.ReadAllBytes(path));
        Assert.False(File.Exists(checkpointFile));
    }

    [Fact]
    public async Task CheckpointPathCanBeChosen()
    {
        var path = Path.Combine(_dir, "disc.iso");
        var checkpoint = Path.Combine(_dir, "elsewhere.state");

        using var cts = new CancellationTokenSource();
        var reader = new FakeSectorReader(OpticalTestData.DiscImage(300));
        reader.BeforeRead = (lba, count) =>
        {
            if (lba >= 128 && count == 64)
            {
                cts.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new DiscRipper().RipToFileAsync(reader, path, Fast with { CheckpointPath = checkpoint }, null, cts.Token));

        Assert.True(File.Exists(checkpoint));
        Assert.False(File.Exists(path + ".btxrip"));
    }

    [Fact]
    public void FileStoreRoundTrips()
    {
        var store = new FileRipCheckpointStore(Path.Combine(_dir, "a.btxrip"));
        var checkpoint = new RipCheckpoint
        {
            SectorCount = 2_295_104,
            NextSector = 1_000_000,
            BadSectors = [new SectorRange(10, 3), new SectorRange(500_000, 1)],
        };

        store.Save(checkpoint);
        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(checkpoint.SectorCount, loaded.SectorCount);
        Assert.Equal(checkpoint.NextSector, loaded.NextSector);
        Assert.Equal(checkpoint.BadSectors, loaded.BadSectors);
        store.Clear();
        Assert.Null(store.Load());
    }

    [Fact]
    public void MissingCheckpointIsNull()
    {
        Assert.Null(new FileRipCheckpointStore(Path.Combine(_dir, "none")).Load());
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("{\"version\": 99, \"sectorCount\": 5, \"nextSector\": 2}")]
    [InlineData("{\"version\": 1, \"sectorCount\": \"many\"}")]
    public void DamagedOrForeignCheckpointsAreIgnored(string content)
    {
        var path = Path.Combine(_dir, "bad.btxrip");
        File.WriteAllText(path, content);

        Assert.Null(new FileRipCheckpointStore(path).Load());
    }

    [Fact]
    public void SavingTwiceReplacesTheFile()
    {
        var store = new FileRipCheckpointStore(Path.Combine(_dir, "b.btxrip"));
        store.Save(new RipCheckpoint { SectorCount = 10, NextSector = 3 });
        store.Save(new RipCheckpoint { SectorCount = 10, NextSector = 7 });

        Assert.Equal(7, store.Load()!.NextSector);
        Assert.Single(Directory.GetFiles(_dir, "b.btxrip*"));
    }

    [Fact]
    public void ReaderStreamExposesTheDiscAsBytes()
    {
        var image = OpticalTestData.DiscImage(100);
        using var stream = new SectorReaderStream(new FakeSectorReader(image));

        Assert.Equal(image.Length, stream.Length);
        stream.Position = 10_000;
        var piece = new byte[3000];
        stream.ReadExactly(piece);
        Assert.Equal(image.AsSpan(10_000, 3000).ToArray(), piece);

        stream.Seek(-100, SeekOrigin.End);
        Assert.Equal(100, stream.Read(new byte[500]));
    }

    [Fact]
    public void ReaderStreamReportsUnreadableSectorsAsIoErrors()
    {
        var reader = new FakeSectorReader(OpticalTestData.DiscImage(100));
        reader.PermanentlyBad.Add(50);
        using var stream = new SectorReaderStream(reader);

        stream.Position = 49 * 2048;
        stream.ReadExactly(new byte[2048]);
        stream.Position = 50 * 2048;

        Assert.Throws<IOException>(() => stream.ReadExactly(new byte[2048]));
        // the neighbours of the bad sector stay readable
        stream.Position = 51 * 2048;
        stream.ReadExactly(new byte[2048]);
    }
}
