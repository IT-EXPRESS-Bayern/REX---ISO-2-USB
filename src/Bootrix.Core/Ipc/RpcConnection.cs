// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bootrix.Core.Errors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Core.Ipc;

/// <summary>
/// A bidirectional request/response channel over a byte stream. Either side can call methods and send
/// notifications; calls run in parallel and are matched to their answers by id. When the stream ends,
/// for whatever reason, every open call fails with <see cref="RpcConnectionClosedException"/> and every
/// call being served is aborted, so nothing is left hanging.
/// </summary>
/// <remarks>
/// Payloads are never logged. Log lines carry method names, ids and sizes only, because requests may
/// contain secrets such as passwords for local accounts.
/// </remarks>
public sealed partial class RpcConnection : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly FrameStream _frames;
    private readonly RpcConnectionOptions _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<long, TaskCompletionSource<WireMessage>> _pending = [];
    private readonly Dictionary<long, InboundCall> _inbound = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private RpcConnectionClosedException? _closed;
    private bool _started;
    private long _nextId;
    private long _lastReceived;
    private Task _readLoop = Task.CompletedTask;
    private Task _heartbeat = Task.CompletedTask;

    public RpcConnection(Stream stream, RpcHandlerTable? handlers = null, RpcConnectionOptions? options = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _options = options ?? new RpcConnectionOptions();
        _frames = new FrameStream(stream, _options.MaxFrameBytes);
        _logger = logger ?? NullLogger.Instance;
        _time = _options.TimeProvider;
        Handlers = handlers ?? new RpcHandlerTable();
    }

    /// <summary>The methods this side answers. Complete it before <see cref="Start"/>.</summary>
    public RpcHandlerTable Handlers { get; }

    /// <summary>Completes once the connection is closed and every call that was being served has returned. Never faults.</summary>
    public Task Completion => _completion.Task;

    public bool IsClosed
    {
        get
        {
            lock (_gate)
            {
                return _closed is not null;
            }
        }
    }

    /// <summary>Why the connection ended; null for an orderly close.</summary>
    public Exception? CloseReason { get; private set; }

    public void Start()
    {
        lock (_gate)
        {
            if (_started)
            {
                throw new InvalidOperationException("The connection was already started.");
            }

            _started = true;
        }

        Handlers.Freeze();
        _lastReceived = _time.GetTimestamp();
        _readLoop = Task.Run(ReadLoopAsync);
        if (_options.HeartbeatInterval is { } interval)
        {
            _heartbeat = Task.Run(() => HeartbeatLoopAsync(interval));
        }
    }

    /// <summary>Calls a method and waits for its result. Cancelling tells the peer to stop and ends the wait at once.</summary>
    public async Task<TResult> InvokeAsync<TResult>(string method, object? parameters, CancellationToken cancellationToken = default)
    {
        var response = await CallAsync(method, parameters, cooperative: false, cancellationToken, CancellationToken.None).ConfigureAwait(false);
        return ReadResult<TResult>(response);
    }

    public async Task InvokeAsync(string method, object? parameters, CancellationToken cancellationToken = default)
    {
        await CallAsync(method, parameters, cooperative: false, cancellationToken, CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// For long calls whose peer cleans up before it answers. <paramref name="cancellationToken"/> sends a
    /// cancel and <paramref name="abortToken"/> an abort, but the call only ends with the peer's answer
    /// (which may report the cancellation) or when the connection is lost.
    /// </summary>
    public async Task<TResult> InvokeCooperativeAsync<TResult>(
        string method,
        object? parameters,
        CancellationToken cancellationToken,
        CancellationToken abortToken)
    {
        var response = await CallAsync(method, parameters, cooperative: true, cancellationToken, abortToken).ConfigureAwait(false);
        return ReadResult<TResult>(response);
    }

    /// <summary>Notifications of one sender arrive in the order the sends were awaited.</summary>
    public Task NotifyAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        EnsureStarted();
        return SendAsync(new WireMessage { Kind = WireKind.Notification, Method = method, Params = Serialize(parameters) }, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync(null).ConfigureAwait(false);
        await Task.WhenAll(_readLoop, _heartbeat).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _lifetime.Dispose();
        _frames.Dispose();
    }

    private async Task<WireMessage> CallAsync(string method, object? parameters, bool cooperative, CancellationToken cancellationToken, CancellationToken abortToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        EnsureStarted();
        cancellationToken.ThrowIfCancellationRequested();
        abortToken.ThrowIfCancellationRequested();

        var id = Interlocked.Increment(ref _nextId);
        var pending = new TaskCompletionSource<WireMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            if (_closed is not null)
            {
                throw _closed;
            }

            _pending.Add(id, pending);
        }

        CancellationTokenRegistration onCancel = default;
        CancellationTokenRegistration onAbort = default;
        try
        {
            await SendAsync(
                new WireMessage { Kind = WireKind.Request, Id = id, Method = method, Params = Serialize(parameters) },
                cancellationToken).ConfigureAwait(false);

            // Registered after the request went out so the peer never sees a cancel for a call it does not know yet.
            onCancel = cancellationToken.Register(() => InterruptCall(RpcMethods.Cancel, id, pending, cooperative, cancellationToken));
            onAbort = abortToken.Register(() => InterruptCall(RpcMethods.Abort, id, pending, cooperative, abortToken));
            return await pending.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _pending.Remove(id);
            }

            await onCancel.DisposeAsync().ConfigureAwait(false);
            await onAbort.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void InterruptCall(string control, long id, TaskCompletionSource<WireMessage> pending, bool cooperative, CancellationToken token)
    {
        _ = SendControlAsync(control, new CancelParams(id));
        if (!cooperative)
        {
            pending.TrySetCanceled(token);
        }
    }

    private async Task SendControlAsync(string control, object? parameters = null)
    {
        try
        {
            await NotifyAsync(control, parameters).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RpcConnectionClosedException or RpcProtocolException)
        {
            // The peer is gone or cannot take the message; there is nothing left to cancel.
        }
    }

    private static T ReadResult<T>(WireMessage response)
    {
        if (response.Error is { } error)
        {
            throw RpcErrors.FromWire(error);
        }

        if (response.Result is not { } element)
        {
            throw new RpcProtocolException("response has no result");
        }

        try
        {
            return element.Deserialize<T>(IpcJson.Options) ?? throw new RpcProtocolException("result is null");
        }
        catch (JsonException ex)
        {
            throw new RpcProtocolException("result is invalid", ex);
        }
    }

    private static JsonElement? Serialize(object? parameters) =>
        parameters is null ? null : JsonSerializer.SerializeToElement(parameters, parameters.GetType(), IpcJson.Options);

    private void EnsureStarted()
    {
        lock (_gate)
        {
            if (!_started)
            {
                throw new InvalidOperationException("The connection was not started.");
            }

            if (_closed is not null)
            {
                throw _closed;
            }
        }
    }

    private async Task SendAsync(WireMessage message, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_closed is not null)
            {
                throw _closed;
            }
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, IpcJson.Options);
        try
        {
            await _frames.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or RpcProtocolException))
        {
            // A failed write leaves the stream out of step, so the connection cannot be used any more.
            _ = CloseAsync(ex);
            throw _closed ?? new RpcConnectionClosedException("write failed", ex);
        }
    }

    private async Task ReadLoopAsync()
    {
        Exception? cause = null;
        try
        {
            while (true)
            {
                var frame = await _frames.ReadAsync(_lifetime.Token).ConfigureAwait(false);
                if (frame is null)
                {
                    break;
                }

                Volatile.Write(ref _lastReceived, _time.GetTimestamp());
                Dispatch(frame);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // Closed from this side.
        }
        catch (Exception ex)
        {
            cause = ex;
        }

        await CloseAsync(cause).ConfigureAwait(false);
    }

    private async Task HeartbeatLoopAsync(TimeSpan interval)
    {
        using var timer = new PeriodicTimer(interval, _time);
        Task? pingInFlight = null;
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                if (_time.GetElapsedTime(Volatile.Read(ref _lastReceived)) > _options.HeartbeatTimeout)
                {
                    await CloseAsync(new TimeoutException("no sign of life from the peer")).ConfigureAwait(false);
                    return;
                }

                // A peer that stopped reading can make a write block for good (unbuffered pipes on Windows).
                // The ping must not hold up the next silence check, which is what ends such a connection.
                if (pingInFlight is null || pingInFlight.IsCompleted)
                {
                    pingInFlight = SendPingAsync();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Closed.
        }
    }

    private async Task SendPingAsync()
    {
        try
        {
            await SendControlAsync(RpcMethods.Ping).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await CloseAsync(ex).ConfigureAwait(false);
        }
    }

    private Task CloseAsync(Exception? cause)
    {
        List<TaskCompletionSource<WireMessage>> pending;
        List<InboundCall> serving;
        RpcConnectionClosedException closed;
        lock (_gate)
        {
            if (_closed is not null)
            {
                return _completion.Task;
            }

            closed = cause as RpcConnectionClosedException
                ?? new RpcConnectionClosedException(cause?.Message ?? "connection closed", cause);
            _closed = closed;
            CloseReason = cause;
            pending = [.. _pending.Values];
            serving = [.. _inbound.Values];
        }

        return FinishCloseAsync(closed, pending, serving);
    }

    private async Task FinishCloseAsync(RpcConnectionClosedException closed, List<TaskCompletionSource<WireMessage>> pending, List<InboundCall> serving)
    {
        foreach (var call in pending)
        {
            call.TrySetException(closed);
        }

        // A job whose caller vanished must stop and clean up now, not at some later point.
        foreach (var call in serving)
        {
            call.Abort();
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The stream is already broken, which is why we are here.
        }

        await Task.WhenAll(serving.Select(c => c.Done.Task)).ConfigureAwait(false);
        _logger.LogInformation("Connection closed ({Reason})", CloseReason?.GetType().Name ?? "orderly");
        _completion.TrySetResult();
    }
}
