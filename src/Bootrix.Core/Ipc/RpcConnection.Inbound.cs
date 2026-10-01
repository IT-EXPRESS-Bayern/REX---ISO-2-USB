// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bootrix.Core.Errors;
using Microsoft.Extensions.Logging;

namespace Bootrix.Core.Ipc;

// The receiving side: what is done with each frame that arrives, and how a request is served.
public sealed partial class RpcConnection
{
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

    private static string Truncate(string? text) => text is { Length: > 64 } ? text[..64] + "..." : text ?? "";
}
