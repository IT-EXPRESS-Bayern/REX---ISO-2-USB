// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Core.Ipc;

public sealed record RpcServerOptions
{
    /// <summary>Clients served at the same time. The next client is not accepted before a slot is free.</summary>
    public int MaxClients { get; init; } = 1;

    /// <summary>Time without any client, counted from the start and again after the last client left, after which the server stops. Null means it waits forever.</summary>
    public TimeSpan? IdleTimeout { get; init; }

    /// <summary>Called once when the server stops because of <see cref="IdleTimeout"/>.</summary>
    public Action? OnIdle { get; init; }

    public RpcConnectionOptions Connection { get; init; } = new();
}

public enum RpcServerStopReason
{
    Canceled,
    Idle,
}

/// <summary>
/// Gives every accepted connection its handlers. Returns something to dispose when the connection
/// ends (an unsubscribe, a per-client state), or null.
/// </summary>
public delegate IDisposable? RpcSessionFactory(RpcConnection connection, RpcHandlerTable handlers);

/// <summary>Accepts clients from a listener and serves each on an <see cref="RpcConnection"/>.</summary>
public sealed class RpcServer
{
    private readonly IConnectionListener _listener;
    private readonly RpcSessionFactory _sessionFactory;
    private readonly RpcServerOptions _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly List<RpcConnection> _connections = [];
    private int _active;

    public RpcServer(IConnectionListener listener, RpcSessionFactory sessionFactory, RpcServerOptions? options = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(listener);
        ArgumentNullException.ThrowIfNull(sessionFactory);
        _options = options ?? new RpcServerOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(_options.MaxClients, 1);
        _listener = listener;
        _sessionFactory = sessionFactory;
        _logger = logger ?? NullLogger.Instance;
        _time = _options.Connection.TimeProvider;
    }

    /// <summary>Number of clients being served right now.</summary>
    public int ActiveClients
    {
        get
        {
            lock (_gate)
            {
                return _active;
            }
        }
    }

    /// <summary>Serves clients until <paramref name="cancellationToken"/> is cancelled or the idle timeout passes. Open connections are closed on the way out.</summary>
    public async Task<RpcServerStopReason> RunAsync(CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var slots = new SemaphoreSlim(_options.MaxClients);
        var idle = 0;
        var serving = new List<Task>();

        using var idleTimer = _time.CreateTimer(
            _ =>
            {
                Volatile.Write(ref idle, 1);
                stop.Cancel();
            },
            null,
            _options.IdleTimeout ?? Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);

        // Counting and arming happen under one lock: the count reaching zero and the timer running are one step.
        void ClientArrived()
        {
            lock (_gate)
            {
                _active++;
                idleTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }

        void ClientLeft()
        {
            lock (_gate)
            {
                if (--_active == 0 && _options.IdleTimeout is { } timeout)
                {
                    idleTimer.Change(timeout, Timeout.InfiniteTimeSpan);
                }
            }
        }

        try
        {
            while (!stop.IsCancellationRequested)
            {
                await slots.WaitAsync(stop.Token).ConfigureAwait(false);
                Stream stream;
                try
                {
                    stream = await _listener.AcceptAsync(stop.Token).ConfigureAwait(false);
                }
                catch
                {
                    slots.Release();
                    throw;
                }

                ClientArrived();
                serving.Add(ServeAsync(stream, slots, ClientLeft));
                serving.RemoveAll(t => t.IsCompleted);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Stopped by the caller or by the idle timer.
        }
        finally
        {
            await CloseAllAsync().ConfigureAwait(false);
            await Task.WhenAll(serving).ConfigureAwait(false);
        }

        if (Volatile.Read(ref idle) == 1 && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("No client for {Timeout}, stopping", _options.IdleTimeout);
            _options.OnIdle?.Invoke();
            return RpcServerStopReason.Idle;
        }

        return RpcServerStopReason.Canceled;
    }

    private async Task ServeAsync(Stream stream, SemaphoreSlim slots, Action onClientLeft)
    {
        RpcConnection? connection = null;
        IDisposable? session = null;
        try
        {
            connection = new RpcConnection(stream, options: _options.Connection, logger: _logger);
            lock (_gate)
            {
                _connections.Add(connection);
            }

            _logger.LogInformation("Client connected");
            session = _sessionFactory(connection, connection.Handlers);
            connection.Start();
            await connection.Completion.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError("Serving a client failed with {ExceptionType}", ex.GetType().Name);
        }
        finally
        {
            session?.Dispose();
            if (connection is not null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                lock (_gate)
                {
                    _connections.Remove(connection);
                }
            }
            else
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            _logger.LogInformation("Client disconnected");

            // The stream has to be gone before the slot is free: a named pipe with one instance cannot be recreated earlier.
            slots.Release();
            onClientLeft();
        }
    }

    private async Task CloseAllAsync()
    {
        RpcConnection[] open;
        lock (_gate)
        {
            open = [.. _connections];
        }

        await Task.WhenAll(open.Select(c => c.DisposeAsync().AsTask())).ConfigureAwait(false);
    }
}
