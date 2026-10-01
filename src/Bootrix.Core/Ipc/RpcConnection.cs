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
public sealed class RpcConnection : IAsyncDisposable
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
        try
        {
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                if (_time.GetElapsedTime(Volatile.Read(ref _lastReceived)) > _options.HeartbeatTimeout)
                {
                    await CloseAsync(new TimeoutException("no sign of life from the peer")).ConfigureAwait(false);
                    return;
                }

                await SendControlAsync(RpcMethods.Ping).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Closed.
        }
    }

    private void Dispatch(byte[] frame)
    {
        WireMessage message;
        try
        {
            message = JsonSerializer.Deserialize<WireMessage>(frame, IpcJson.Options)
                ?? throw new RpcProtocolException("message is empty");
        }
        catch (JsonException ex)
        {
            throw new RpcProtocolException("message is not valid", ex);
        }

        switch (message.Kind)
        {
            case WireKind.Request:
                StartRequest(message, frame.Length);
                break;
            case WireKind.Response:
                Complete(message);
                break;
            case WireKind.Notification:
                HandleNotification(message);
                break;
            default:
                throw new RpcProtocolException("unknown message kind");
        }
    }

    private void Complete(WireMessage response)
    {
        TaskCompletionSource<WireMessage>? pending;
        lock (_gate)
        {
            _pending.Remove(response.Id, out pending);
        }

        // Unknown ids are answers to calls that were cancelled locally in the meantime.
        pending?.TrySetResult(response);
    }

    private void HandleNotification(WireMessage message)
    {
        if (string.IsNullOrEmpty(message.Method))
        {
            throw new RpcProtocolException("notification without method");
        }

        switch (message.Method)
        {
            case RpcMethods.Ping:
                return;
            case RpcMethods.Cancel:
                ApplyInterrupt(message, abort: false);
                return;
            case RpcMethods.Abort:
                ApplyInterrupt(message, abort: true);
                return;
        }

        if (!Handlers.TryGetNotification(message.Method, out var handler))
        {
            _logger.LogDebug("Ignoring notification {Method}", Truncate(message.Method));
            return;
        }

        try
        {
            handler!(message.Params);
        }
        catch (Exception ex)
        {
            // Only the type is logged: the message of an exception raised while reading parameters may quote them.
            _logger.LogWarning("Notification handler for {Method} failed with {ExceptionType}", Truncate(message.Method), ex.GetType().Name);
        }
    }

    private void ApplyInterrupt(WireMessage message, bool abort)
    {
        CancelParams? parameters;
        try
        {
            parameters = message.Params?.Deserialize<CancelParams>(IpcJson.Options);
        }
        catch (JsonException ex)
        {
            throw new RpcProtocolException("cancel is not valid", ex);
        }

        if (parameters is null)
        {
            throw new RpcProtocolException("cancel without id");
        }

        InboundCall? call;
        lock (_gate)
        {
            _inbound.TryGetValue(parameters.Id, out call);
        }

        if (call is null)
        {
            return;
        }

        // Cancellation callbacks run synchronously; keep them off the thread that reads the connection.
        _ = Task.Run(() =>
        {
            try
            {
                if (abort)
                {
                    call.Abort();
                }
                else
                {
                    call.Cancel();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("A cancellation callback of request {Id} failed with {ExceptionType}", parameters.Id, ex.GetType().Name);
            }
        });
    }

    private void StartRequest(WireMessage request, int size)
    {
        if (request.Id <= 0 || string.IsNullOrEmpty(request.Method))
        {
            throw new RpcProtocolException("request without id or method");
        }

        var call = new InboundCall();
        lock (_gate)
        {
            if (_closed is not null)
            {
                call.Dispose();
                return;
            }

            if (!_inbound.TryAdd(request.Id, call))
            {
                call.Dispose();
                throw new RpcProtocolException("duplicate request id");
            }
        }

        _logger.LogDebug("Request {Id} {Method} received ({Size} bytes)", request.Id, Truncate(request.Method), size);
        _ = Task.Run(() => ServeAsync(request, call));
    }

    private async Task ServeAsync(WireMessage request, InboundCall call)
    {
        var fatal = false;
        try
        {
            WireMessage response;
            try
            {
                var result = await InvokeHandlerAsync(request, call).ConfigureAwait(false);
                response = new WireMessage { Kind = WireKind.Response, Id = request.Id, Result = result };
            }
            catch (Exception ex)
            {
                fatal = ex is RpcFatalException;
                response = Failure(request, ex);
            }

            await SendResponseAsync(request, response).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError("Serving request {Id} {Method} failed with {ExceptionType}", request.Id, Truncate(request.Method), ex.GetType().Name);
        }
        finally
        {
            lock (_gate)
            {
                _inbound.Remove(request.Id);
            }

            call.Dispose();
            call.Done.TrySetResult();
        }

        if (fatal)
        {
            _ = CloseAsync(new RpcProtocolException("the peer was rejected"));
        }
    }

    private async Task SendResponseAsync(WireMessage request, WireMessage response)
    {
        try
        {
            await SendAsync(response, CancellationToken.None).ConfigureAwait(false);
        }
        catch (RpcProtocolException ex)
        {
            // The answer does not fit into a frame; the caller still deserves one.
            _logger.LogWarning("Response to {Method} ({Id}) was not sent: {Reason}", Truncate(request.Method), request.Id, ex.Detail);
            await TrySendAsync(Failure(request, ex)).ConfigureAwait(false);
        }
        catch (RpcConnectionClosedException)
        {
            // The caller is gone; there is nobody to answer.
        }
    }

    private async Task TrySendAsync(WireMessage message)
    {
        try
        {
            await SendAsync(message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RpcConnectionClosedException or RpcProtocolException)
        {
            // Nothing more can be done for this call.
        }
    }

    private async Task<JsonElement?> InvokeHandlerAsync(WireMessage request, InboundCall call)
    {
        if (!Handlers.TryGetRequest(request.Method!, out var handler))
        {
            throw new RpcProtocolException($"unknown method '{Truncate(request.Method!)}'");
        }

        var context = new RpcCallContext(this, request.Id, request.Method!, call.SoftToken, call.AbortToken);
        return await handler!(request.Params, context).ConfigureAwait(false);
    }

    private WireMessage Failure(WireMessage request, Exception exception)
    {
        // Messages of unexpected exceptions can quote request data, so only the type and the stack go to the log.
        if (exception is BootrixException known)
        {
            _logger.LogInformation("Request {Id} {Method} failed with {Code}", request.Id, Truncate(request.Method), known.Code);
        }
        else if (exception is OperationCanceledException)
        {
            _logger.LogInformation("Request {Id} {Method} was cancelled", request.Id, Truncate(request.Method));
        }
        else
        {
            _logger.LogError("Request {Id} {Method} failed unexpectedly with {ExceptionType}\n{Stack}", request.Id, Truncate(request.Method), exception.GetType().Name, exception.StackTrace);
        }

        return new WireMessage { Kind = WireKind.Response, Id = request.Id, Error = RpcErrors.ToWire(exception) };
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

    private static string Truncate(string? text) => text is { Length: > 64 } ? text[..64] + "..." : text ?? "";
}
