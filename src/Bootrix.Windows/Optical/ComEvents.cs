// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Bootrix.Windows.Optical;

/// <summary>
/// Hooks a delegate to an event of a COM object. IMAPI reports progress through dispinterfaces
/// (DDiscFormat2DataEvents and friends), which the object announces on a connection point and delivers
/// by DISPID through IDispatch::Invoke. The runtime's ComEventsHelper contains exactly that sink, so no
/// IDispatch implementation of our own is needed; the events arrive on a thread of IMAPI's choosing.
/// </summary>
internal sealed class ComEvents : IDisposable
{
    private readonly object _source;
    private readonly Guid _interfaceId;
    private readonly List<(int DispatchId, Delegate Handler)> _handlers = [];

    public ComEvents(object source, Guid interfaceId)
    {
        _source = source;
        _interfaceId = interfaceId;
    }

    public void On(int dispatchId, Delegate handler)
    {
        ComEventsHelper.Combine(_source, _interfaceId, dispatchId, handler);
        _handlers.Add((dispatchId, handler));
    }

    /// <summary>
    /// Like <see cref="On"/>, but a source that refuses the connection only costs the notifications: the burn or erase itself
    /// does not depend on them, so it carries on without progress rather than failing.
    /// </summary>
    public bool TryOn(int dispatchId, Delegate handler, ILogger logger)
    {
        try
        {
            On(dispatchId, handler);
            return true;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or NotSupportedException)
        {
            logger.LogWarning(ex, "Notifications {DispatchId:X} could not be connected; progress will not be reported", dispatchId);
            return false;
        }
    }

    public void Dispose()
    {
        foreach (var (dispatchId, handler) in _handlers)
        {
            ComEventsHelper.Remove(_source, _interfaceId, dispatchId, handler);
        }

        _handlers.Clear();
    }
}
