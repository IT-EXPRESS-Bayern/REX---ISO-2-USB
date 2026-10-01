// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Optical;
using Bootrix.Core.Optical.Images;
using Bootrix.Core.Optical.Reading;
using Bootrix.Windows.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Windows.Optical;

/// <summary>
/// Optical drives on Windows: recorders, discs, burning and erasing through IMAPI2, reading through the volume.
/// Every IMAPI operation runs on a thread of its own in the multithreaded apartment (see <see cref="MtaWorker"/>),
/// so none of these methods blocks the caller or needs it to have a message loop.
/// </summary>
public sealed class ImapiOpticalService : IOpticalService, IDisposable
{
    private readonly ILogger _logger;
    private readonly ILogger<DiscRipper> _ripperLogger;
    private readonly ImapiBurner _burner;
    private readonly ImapiEraser _eraser;
    private readonly object _watchLock = new();
    private ImapiDriveWatcher? _watcher;
    private EventHandler? _drivesChanged;

    public ImapiOpticalService(ILogger<ImapiOpticalService> logger, ILogger<DiscRipper>? ripperLogger = null)
    {
        _logger = logger;
        _ripperLogger = ripperLogger ?? NullLogger<DiscRipper>.Instance;
        _burner = new ImapiBurner(logger);
        _eraser = new ImapiEraser(logger);
    }

    /// <summary>Raised when a drive appears or goes; the notifications are only requested from IMAPI while somebody listens.</summary>
    public event EventHandler? DrivesChanged
    {
        add
        {
            lock (_watchLock)
            {
                _drivesChanged += value;
                if (_watcher is null)
                {
                    _watcher = new ImapiDriveWatcher(_logger);
                    _watcher.Changed += OnWatcherChanged;
                }
            }
        }
        remove
        {
            lock (_watchLock)
            {
                _drivesChanged -= value;
                if (_drivesChanged is null && _watcher is not null)
                {
                    _watcher.Changed -= OnWatcherChanged;
                    _watcher.Dispose();
                    _watcher = null;
                }
            }
        }
    }

    public Task<IReadOnlyList<OpticalDrive>> EnumerateDrivesAsync(CancellationToken cancellationToken = default) =>
        Run("IMAPI drives", () => ImapiDrives.Enumerate(_logger), cancellationToken);

    public Task<OpticalMedia> QueryMediaAsync(OpticalDrive drive, CancellationToken cancellationToken = default) =>
        Run(
            "IMAPI media",
            () =>
            {
                using var com = new ComScope();
                var recorder = ImapiDrives.CreateRecorder(com, drive.Id);
                return ImapiMedia.Query(com, recorder) ?? WindowsSectorReader.ProbeMedia(drive);
            },
            cancellationToken);

    public Task BurnImageAsync(
        OpticalDrive drive,
        DiscImageSource image,
        BurnOptions options,
        IProgress<BurnProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options.Validate();
        return Run(
            "IMAPI burn",
            () =>
            {
                using var sleep = new SleepGuard();
                using var com = new ComScope();
                using var source = ImapiStream.FromImage(image);
                var recorder = ImapiDrives.CreateRecorder(com, drive.Id);
                _burner.Burn(com, recorder, drive, source.Stream, source.Sectors, options, progress, cancellationToken);
                return true;
            },
            cancellationToken);
    }

    public Task BurnFolderAsync(
        OpticalDrive drive,
        FolderBurnRequest request,
        BurnOptions options,
        IProgress<BurnProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options.Validate();
        return Run(
            "IMAPI folder burn",
            () =>
            {
                using var sleep = new SleepGuard();
                using var com = new ComScope();
                var recorder = ImapiDrives.CreateRecorder(com, drive.Id);

                var family = ImapiMedia.Query(com, recorder)?.Family ?? OpticalMediaFamily.Dvd;
                var fileSystems = request.FileSystems ?? FolderBurnPlanner.DefaultFileSystems(family);
                var label = FolderBurnPlanner.NormalizeLabel(options.VolumeLabel, fileSystems, Path.GetFileName(request.SourceFolder.TrimEnd('\\', '/')));

                using var image = ImapiFolderBuilder.Build(com, recorder, request, fileSystems, label);
                _burner.Burn(com, recorder, drive, image.Stream, image.Sectors, options, progress, cancellationToken);
                return true;
            },
            cancellationToken);
    }

    public Task EraseAsync(
        OpticalDrive drive,
        EraseMode mode,
        IProgress<EraseProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Run(
            "IMAPI erase",
            () =>
            {
                using var sleep = new SleepGuard();
                using var com = new ComScope();
                var recorder = ImapiDrives.CreateRecorder(com, drive.Id);
                _eraser.Erase(com, recorder, drive, mode, progress, cancellationToken);
                return true;
            },
            cancellationToken);

    public ISectorReader OpenSectorReader(OpticalDrive drive) => WindowsSectorReader.Open(drive);

    public async Task<RipReport> RipToIsoAsync(
        OpticalDrive drive,
        string isoPath,
        RipOptions options,
        IProgress<RipProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        using var sleep = new SleepGuard();
        using var reader = WindowsSectorReader.Open(drive);
        return await new DiscRipper(_ripperLogger).RipToFileAsync(reader, isoPath, options, progress, cancellationToken).ConfigureAwait(false);
    }

    public Task EjectAsync(OpticalDrive drive, CancellationToken cancellationToken = default) =>
        Run(
            "IMAPI eject",
            () =>
            {
                using var com = new ComScope();
                ImapiDrives.CreateRecorder(com, drive.Id).EjectMedia();
                return true;
            },
            cancellationToken);

    public Task CloseTrayAsync(OpticalDrive drive, CancellationToken cancellationToken = default)
    {
        if (!drive.CanLoadMedia)
        {
            throw new NotSupportedException($"{drive.DisplayName} is a slot-loading drive and cannot close its tray by command.");
        }

        return Run(
            "IMAPI close tray",
            () =>
            {
                using var com = new ComScope();
                ImapiDrives.CreateRecorder(com, drive.Id).CloseTray();
                return true;
            },
            cancellationToken);
    }

    public void Dispose()
    {
        lock (_watchLock)
        {
            _watcher?.Dispose();
            _watcher = null;
            _drivesChanged = null;
        }
    }

    private void OnWatcherChanged(object? sender, EventArgs e) => _drivesChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Runs COM work on an MTA thread and turns IMAPI failures into Bootrix errors.</summary>
    private static Task<T> Run<T>(string name, Func<T> work, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }

        return MtaWorker.RunAsync(
            () =>
            {
                try
                {
                    return work();
                }
                catch (Exception ex) when (ex is not (BootrixException or OperationCanceledException))
                {
                    throw ImapiErrors.Translate(ex, new ImapiErrorContext { Cancellation = cancellationToken });
                }
            },
            name);
    }
}
