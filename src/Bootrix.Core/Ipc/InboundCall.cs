// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Ipc;

/// <summary>The cancellation state of one call being served; cancel and abort may arrive at any time, even after the call ended.</summary>
internal sealed class InboundCall : IDisposable
{
    private readonly CancellationTokenSource _abort = new();
    private readonly CancellationTokenSource _soft;
    private readonly Lock _gate = new();
    private bool _disposed;

    public InboundCall()
    {
        _soft = CancellationTokenSource.CreateLinkedTokenSource(_abort.Token);
        SoftToken = _soft.Token;
        AbortToken = _abort.Token;
    }

    public CancellationToken SoftToken { get; }

    public CancellationToken AbortToken { get; }

    public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Cancel()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _soft.Cancel();
            }
        }
    }

    /// <summary>Cancelling the abort source also cancels the soft one, which is linked to it.</summary>
    public void Abort()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                _abort.Cancel();
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _soft.Dispose();
            _abort.Dispose();
        }
    }
}
