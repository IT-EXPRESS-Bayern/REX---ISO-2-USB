// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Engine;

/// <summary>
/// Watches a running job after an abort was requested. A job that is stuck in a driver call on a
/// dying stick does not react to cancellation; when it is still running after the grace period,
/// <paramref name="onStuck"/> is called, which in the broker ends the whole process.
/// </summary>
internal sealed class AbortWatchdog : IDisposable
{
    private readonly CancellationTokenRegistration _registration;
    private readonly Lock _gate = new();
    private readonly TimeSpan _grace;
    private readonly TimeProvider _time;
    private readonly Action _onStuck;
    private ITimer? _timer;
    private bool _disposed;

    public AbortWatchdog(TimeSpan grace, TimeProvider time, Action onStuck, CancellationToken abortToken)
    {
        _grace = grace;
        _time = time;
        _onStuck = onStuck;
        _registration = abortToken.Register(Arm);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
        }

        _registration.Dispose();
    }

    private void Arm()
    {
        lock (_gate)
        {
            if (_disposed || _timer is not null)
            {
                return;
            }

            _timer = _time.CreateTimer(_ => Fire(), null, _grace, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
        }

        _onStuck();
    }
}
