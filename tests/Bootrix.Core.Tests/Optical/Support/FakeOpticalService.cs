// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Optical;
using Bootrix.Core.Optical.Images;
using Bootrix.Core.Optical.Reading;

namespace Bootrix.Core.Tests.Optical.Support;

/// <summary>Recorders and discs in memory. Burning stores the bytes, so reading the disc back returns what was written.</summary>
internal sealed class FakeOpticalService : IOpticalService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, int> _notReady = [];
    private readonly Dictionary<string, Exception> _burnFailures = [];

    public List<OpticalDrive> Drives { get; } = [];

    public Dictionary<string, OpticalMedia> Media { get; } = [];

    /// <summary>What is on each disc; filled by burning, or by the test for ripping.</summary>
    public Dictionary<string, byte[]> Discs { get; } = [];

    public List<string> Calls { get; } = [];

    public List<(OpticalDrive Drive, BurnOptions Options)> Burns { get; } = [];

    public List<(OpticalDrive Drive, FolderBurnRequest Request, BurnOptions Options)> FolderBurns { get; } = [];

    public List<(OpticalDrive Drive, EraseMode Mode)> Erases { get; } = [];

    public List<string> Ejected { get; } = [];

    public RipOptions? LastRipOptions { get; private set; }

    /// <summary>Called for every sector reader that is opened, so a test can damage the disc or watch the reads.</summary>
    public Action<FakeSectorReader>? ReaderCreated { get; set; }

    /// <summary>Lets a test hold the burns until all drives have started, which proves they run at the same time.</summary>
    public CountdownEvent? BurnBarrier { get; set; }

    public bool BurnsStartedTogether { get; private set; } = true;

    public Exception? EjectFailure { get; set; }

    public int OpenAttempts { get; private set; }

    public event EventHandler? DrivesChanged
    {
        add { }
        remove { }
    }

    public OpticalDrive AddDrive(string id, OpticalMedia? media = null, OpticalCapabilities? capabilities = null)
    {
        var drive = new OpticalDrive
        {
            Id = id,
            Vendor = "Test",
            Product = id,
            DriveLetter = id.Length == 1 ? id + ":" : null,
            Capabilities = capabilities ?? (OpticalCapabilities.CdR | OpticalCapabilities.CdRw | OpticalCapabilities.DvdPlusR | OpticalCapabilities.DvdMinusR | OpticalCapabilities.DvdPlusRDualLayer),
        };
        Drives.Add(drive);
        Media[id] = media ?? BlankDvd();
        return drive;
    }

    public static OpticalMedia BlankDvd(long sectors = 2_295_104) => new()
    {
        Type = OpticalMediaType.DvdPlusR,
        State = OpticalMediaState.Blank,
        IsSupported = true,
        TotalSectors = sectors,
        FreeSectors = sectors,
    };

    public void FailBurn(string driveId, Exception error) => _burnFailures[driveId] = error;

    /// <summary>The next <paramref name="times"/> attempts to open the drive for reading fail, as right after a burn.</summary>
    public void NotReady(string driveId, int times) => _notReady[driveId] = times;

    public Task<IReadOnlyList<OpticalDrive>> EnumerateDrivesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<OpticalDrive>>(Drives);

    public Task<OpticalMedia> QueryMediaAsync(OpticalDrive drive, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Calls.Add("query:" + drive.Id);
        }

        return Task.FromResult(Media.GetValueOrDefault(drive.Id, OpticalMedia.None));
    }

    public async Task BurnImageAsync(
        OpticalDrive drive,
        DiscImageSource image,
        BurnOptions options,
        IProgress<BurnProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Burns.Add((drive, options));
            Calls.Add("burn:" + drive.Id);
        }

        await WaitForOtherDrivesAsync(cancellationToken).ConfigureAwait(false);
        if (_burnFailures.TryGetValue(drive.Id, out var failure))
        {
            throw failure;
        }

        using var stream = image.OpenPadded();
        var data = new byte[stream.Length];
        var sectors = (int)(data.Length / 2048);
        for (var sector = 0; sector < sectors; sector += 100)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(100, sectors - sector);
            stream.ReadExactly(data.AsSpan(sector * 2048, count * 2048));
            progress?.Report(new BurnProgress(BurnPhase.Writing, 0, sectors, sector + count, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), 50));
            await Task.Yield();
        }

        progress?.Report(new BurnProgress(BurnPhase.Completed, 0, sectors, sectors, TimeSpan.FromSeconds(2), null, 0));
        lock (_gate)
        {
            Discs[drive.Id] = data;
        }
    }

    public async Task BurnFolderAsync(
        OpticalDrive drive,
        FolderBurnRequest request,
        BurnOptions options,
        IProgress<BurnProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            FolderBurns.Add((drive, request, options));
            Calls.Add("folder:" + drive.Id);
        }

        await WaitForOtherDrivesAsync(cancellationToken).ConfigureAwait(false);
        if (_burnFailures.TryGetValue(drive.Id, out var failure))
        {
            throw failure;
        }

        progress?.Report(new BurnProgress(BurnPhase.Writing, 0, 100, 100, TimeSpan.Zero, null, 0));
    }

    public Task EraseAsync(OpticalDrive drive, EraseMode mode, IProgress<EraseProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Erases.Add((drive, mode));
            Calls.Add("erase:" + drive.Id);
        }

        progress?.Report(new EraseProgress(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4)));
        progress?.Report(new EraseProgress(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(4)));
        Media[drive.Id] = Media[drive.Id] with { State = OpticalMediaState.Blank, HeuristicallyBlank = true };
        return Task.CompletedTask;
    }

    public ISectorReader OpenSectorReader(OpticalDrive drive)
    {
        lock (_gate)
        {
            OpenAttempts++;
            if (_notReady.TryGetValue(drive.Id, out var left) && left > 0)
            {
                _notReady[drive.Id] = left - 1;
                throw new BootrixException(ErrorCode.DeviceNotFound, "not ready");
            }
        }

        var reader = new FakeSectorReader(Discs[drive.Id]);
        ReaderCreated?.Invoke(reader);
        return reader;
    }

    public Task<RipReport> RipToIsoAsync(
        OpticalDrive drive,
        string isoPath,
        RipOptions options,
        IProgress<RipProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        LastRipOptions = options;
        var reader = (FakeSectorReader)OpenSectorReader(drive);
        return new DiscRipper().RipToFileAsync(reader, isoPath, options, progress, cancellationToken);
    }

    public Task EjectAsync(OpticalDrive drive, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            Ejected.Add(drive.Id);
        }

        return EjectFailure is null ? Task.CompletedTask : Task.FromException(EjectFailure);
    }

    public Task CloseTrayAsync(OpticalDrive drive, CancellationToken cancellationToken = default) => Task.CompletedTask;

    private async Task WaitForOtherDrivesAsync(CancellationToken cancellationToken)
    {
        if (BurnBarrier is not { } barrier)
        {
            return;
        }

        barrier.Signal();
        var reached = await Task.Run(() => barrier.Wait(TimeSpan.FromSeconds(5), cancellationToken), cancellationToken).ConfigureAwait(false);
        if (!reached)
        {
            BurnsStartedTogether = false;
        }
    }
}
