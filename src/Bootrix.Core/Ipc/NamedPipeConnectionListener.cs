// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Pipes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Core.Ipc;

/// <summary>
/// Accepts clients on a named pipe. There is always one server instance waiting, the next one is
/// created before the connected one is handed out, so the pipe name never disappears between two
/// clients and nobody else can take it over in that moment. Derived classes add the access control
/// of the platform: the security descriptor of the pipe and the check of the connecting process.
/// </summary>
public class NamedPipeConnectionListener : IConnectionListener
{
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private PendingClient? _pending;
    private int _created;
    private bool _disposed;

    /// <param name="pipeName">Pipe name without the \\.\pipe\ prefix.</param>
    /// <param name="maxClients">Connections that may be open at the same time; one more instance is kept waiting on top of them.</param>
    /// <param name="logger">Receives a warning for every refused client.</param>
    public NamedPipeConnectionListener(string pipeName, int maxClients = 1, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxClients, 1);
        PipeName = pipeName;
        MaxClients = maxClients;
        _logger = logger ?? NullLogger.Instance;
    }

    public string PipeName { get; }

    public int MaxClients { get; }

    public async Task<Stream> AcceptAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            PendingClient current;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                current = _pending ??= StartWaiting();
            }

            await current.Connected.WaitAsync(cancellationToken).ConfigureAwait(false);

            lock (_gate)
            {
                if (ReferenceEquals(_pending, current))
                {
                    _pending = _disposed ? null : StartWaiting();
                }
            }

            if (Authorize(current.Server))
            {
                return current.Server;
            }

            _logger.LogWarning("A client of pipe {Pipe} was refused", PipeName);
            await current.Server.DisposeAsync().ConfigureAwait(false);
        }
    }

    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        PendingClient? waiting;
        lock (_gate)
        {
            _disposed = true;
            waiting = _pending;
            _pending = null;
        }

        return waiting is null ? ValueTask.CompletedTask : waiting.Server.DisposeAsync();
    }

    /// <summary>Creates the next server instance. <paramref name="isFirst"/> is true once, so an override can insist on being the creator of the pipe.</summary>
    protected virtual NamedPipeServerStream CreateServer(bool isFirst) => new(
        PipeName,
        PipeDirection.InOut,
        MaxClients + 1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous);

    /// <summary>Decides whether the client that just connected may talk to us. Refused clients are disconnected and the listener keeps waiting.</summary>
    protected virtual bool Authorize(NamedPipeServerStream pipe) => true;

    private PendingClient StartWaiting()
    {
        var server = CreateServer(isFirst: Interlocked.Exchange(ref _created, 1) == 0);
        var connected = server.WaitForConnectionAsync(CancellationToken.None);

        // An instance that is disposed while it waits ends with an exception nobody is going to look at.
        _ = connected.ContinueWith(static t => t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return new PendingClient(server, connected);
    }

    private sealed record PendingClient(NamedPipeServerStream Server, Task Connected);
}
