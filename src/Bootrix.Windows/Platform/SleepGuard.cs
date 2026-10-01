// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Windows.Interop;

namespace Bootrix.Windows.Platform;

/// <summary>
/// Keeps Windows from going to sleep while a long write, download or build is running.
/// SetThreadExecutionState belongs to the calling thread, so a dedicated thread holds it; disposing
/// the guard from any other thread (a job usually ends elsewhere than it started) still releases it.
/// </summary>
public sealed class SleepGuard : IDisposable
{
    private readonly ManualResetEventSlim _release = new(false);
    private readonly Thread _thread;

    public SleepGuard()
    {
        using var ready = new ManualResetEventSlim(false);
        _thread = new Thread(() =>
        {
            _ = Kernel32.SetThreadExecutionState(Kernel32.EsContinuous | Kernel32.EsSystemRequired | Kernel32.EsAwayModeRequired);
            ready.Set();
            _release.Wait();
            _ = Kernel32.SetThreadExecutionState(Kernel32.EsContinuous);
        })
        {
            IsBackground = true,
            Name = "Bootrix sleep guard",
        };
        _thread.Start();
        ready.Wait();
    }

    public void Dispose()
    {
        _release.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
        _release.Dispose();
    }
}
