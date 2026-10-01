// SPDX-License-Identifier: GPL-3.0-or-later
using System.Threading.Channels;
using Bootrix.Core.Ipc;
using Bootrix.Core.Jobs;

namespace Bootrix.Core.Engine;

/// <summary>
/// Forwards progress of a job to the client. A job reports from its worker thread and must never wait
/// for a slow pipe, so only the latest report is kept: when the client falls behind, it skips ahead
/// instead of working through a queue of outdated percentages.
/// </summary>
internal sealed class ProgressPump : IProgress<ProgressReport>
{
    private readonly RpcConnection _connection;
    private readonly string _runId;
    private readonly Lock _gate = new();
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly Task _pump;
    private ProgressReport? _latest;

    public ProgressPump(RpcConnection connection, string runId)
    {
        _connection = connection;
        _runId = runId;
        _pump = Task.Run(PumpAsync);
    }

    public void Report(ProgressReport value)
    {
        lock (_gate)
        {
            _latest = value;
        }

        _signal.Writer.TryWrite(true);
    }

    /// <summary>Sends what is still pending and stops. Call it before the answer of the job goes out so no progress arrives after the result.</summary>
    public Task CompleteAsync()
    {
        _signal.Writer.TryComplete();
        return _pump;
    }

    private async Task PumpAsync()
    {
        await foreach (var _ in _signal.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            ProgressReport? report;
            lock (_gate)
            {
                report = _latest;
                _latest = null;
            }

            if (report is null)
            {
                continue;
            }

            try
            {
                await _connection.NotifyAsync(BrokerProtocol.Progress, new ProgressNotification(_runId, report.Value)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is RpcConnectionClosedException or RpcProtocolException)
            {
                // Nobody is listening any more; the job is being aborted anyway.
            }
        }
    }
}
