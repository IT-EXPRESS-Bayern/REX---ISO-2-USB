// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Bootrix.Windows.Interop;

namespace Bootrix.Windows.Storage;

/// <summary>
/// Raises <see cref="Changed"/> when a disk or volume arrives or disappears. Plugging in a stick
/// produces a burst of notifications, so events are coalesced.
/// </summary>
internal sealed unsafe class DiskWatcher : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(600);

    private readonly nint[] _registrations = new nint[2];
    private readonly GCHandle _self;
    private readonly Timer _timer;

    public DiskWatcher()
    {
        _timer = new Timer(_ => Changed?.Invoke(this, EventArgs.Empty), null, Timeout.Infinite, Timeout.Infinite);
        _self = GCHandle.Alloc(this);

        var guids = new[] { SetupApi.DiskInterfaceGuid, SetupApi.VolumeInterfaceGuid };
        for (var i = 0; i < guids.Length; i++)
        {
            var filter = new Cfgmgr32.NotifyFilter
            {
                CbSize = (uint)sizeof(Cfgmgr32.NotifyFilter),
                FilterType = Cfgmgr32.FilterTypeDeviceInterface,
                ClassGuid = guids[i],
            };

            var result = Cfgmgr32.RegisterNotification(&filter, GCHandle.ToIntPtr(_self), &OnNotification, out _registrations[i]);
            if (result != Cfgmgr32.CrSuccess)
            {
                _registrations[i] = 0;
            }
        }
    }

    public event EventHandler? Changed;

    public void Dispose()
    {
        foreach (var registration in _registrations.Where(r => r != 0))
        {
            _ = Cfgmgr32.UnregisterNotification(registration);
        }

        _timer.Dispose();
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    [UnmanagedCallersOnly]
    private static uint OnNotification(nint notify, nint context, int action, nint eventData, uint eventDataSize)
    {
        if (action is Cfgmgr32.ActionDeviceInterfaceArrival or Cfgmgr32.ActionDeviceInterfaceRemoval
            && GCHandle.FromIntPtr(context).Target is DiskWatcher watcher)
        {
            watcher._timer.Change(Debounce, Timeout.InfiniteTimeSpan);
        }

        return 0;
    }
}
