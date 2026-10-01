// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;

namespace Bootrix.Windows.Optical;

/// <summary>
/// Collects the COM objects an operation creates and releases them all, newest first, when it ends. The
/// runtime would do it eventually, but a recorder object that stays alive until the next garbage collection
/// keeps the drive reserved for other programs.
/// </summary>
internal sealed class ComScope : IDisposable
{
    private readonly Stack<object> _objects = new();

    public T Add<T>(T comObject)
        where T : class
    {
        if (Marshal.IsComObject(comObject))
        {
            _objects.Push(comObject);
        }

        return comObject;
    }

    public void Dispose()
    {
        while (_objects.TryPop(out var item))
        {
            Marshal.FinalReleaseComObject(item);
        }
    }
}
