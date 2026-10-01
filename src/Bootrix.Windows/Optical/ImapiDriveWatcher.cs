// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.Logging;
using Windows.Win32.Storage.Imapi;

namespace Bootrix.Windows.Optical;

/// <summary>
/// Raises <see cref="Changed"/> when IMAPI reports that a recorder was added or removed (DDiscMaster2Events).
/// The disc master lives on a thread of its own for as long as the watcher does; events arrive on IMAPI's
/// threads, and plugging in a drive sends several, so they are coalesced.
/// </summary>
internal sealed class ImapiDriveWatcher : IDisposable
{
    // DISPID_DDISCMASTER2EVENTS_DEVICE_ADDED and _REMOVED in imapi2.h; the metadata the bindings come from does not carry them.
    private const int DeviceAddedDispatchId = 0x100;
    private const int DeviceRemovedDispatchId = 0x101;

    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);

    private readonly ILogger _logger;
    private readonly Timer _timer;
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _worker;

    public ImapiDriveWatcher(ILogger logger)
    {
        _logger = logger;
        _timer = new Timer(_ => Changed?.Invoke(this, EventArgs.Empty), null, Timeout.Infinite, Timeout.Infinite);

        _worker = MtaWorker.RunAsync(Watch, "IMAPI drive watcher");
        _ready.Task.Wait(TimeSpan.FromSeconds(10));
    }

    public event EventHandler? Changed;

    public void Dispose()
    {
        _stop.Set();
        try
        {
            _worker.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The worker logs its own failure; there is nothing more to do while shutting down.
        }

        _timer.Dispose();
        _stop.Dispose();
    }

    private delegate void DeviceHandler(object sender, string uniqueId);

    private void Watch()
    {
        try
        {
            using var com = new ComScope();
            var master = com.Add(new MsftDiscMaster2());
            using var events = new ComEvents(master, typeof(DDiscMaster2Events).GUID);
            events.On(DeviceAddedDispatchId, new DeviceHandler((_, _) => Schedule()));
            events.On(DeviceRemovedDispatchId, new DeviceHandler((_, _) => Schedule()));
            _ready.TrySetResult();
            _stop.Wait();
        }
        catch (Exception ex)
        {
            // Without events the drive list is simply refreshed by hand; a failing watcher must not break burning.
            _logger.LogWarning(ex, "The IMAPI drive notifications could not be started");
        }
        finally
        {
            _ready.TrySetResult();
        }
    }

    private void Schedule()
    {
        try
        {
            _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Raised during shutdown by an event that was already on its way.
        }
    }
}
