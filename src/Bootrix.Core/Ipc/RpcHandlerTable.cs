// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;

namespace Bootrix.Core.Ipc;

internal delegate Task<JsonElement?> RpcRequestHandler(JsonElement? parameters, RpcCallContext context);

internal delegate void RpcNotificationHandler(JsonElement? parameters);

/// <summary>
/// The methods a side of the connection answers. Every method is registered by name with a
/// typed handler; nothing is looked up by reflection, so a peer can only reach what was added here.
/// </summary>
public sealed class RpcHandlerTable
{
    private readonly Dictionary<string, RpcRequestHandler> _requests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RpcNotificationHandler> _notifications = new(StringComparer.Ordinal);
    private bool _frozen;

    public RpcHandlerTable Add<TParams, TResult>(string method, Func<TParams, RpcCallContext, Task<TResult>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Register(_requests, method, async (raw, context) =>
        {
            var result = await handler(Read<TParams>(raw), context).ConfigureAwait(false);
            return JsonSerializer.SerializeToElement(result, IpcJson.Options);
        });
        return this;
    }

    public RpcHandlerTable Add<TParams>(string method, Func<TParams, RpcCallContext, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Register(_requests, method, async (raw, context) =>
        {
            await handler(Read<TParams>(raw), context).ConfigureAwait(false);
            return null;
        });
        return this;
    }

    /// <summary>Notifications run on the thread that reads the connection, in the order they were sent; keep them short.</summary>
    public RpcHandlerTable AddNotification<TParams>(string method, Action<TParams> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Register(_notifications, method, raw => handler(Read<TParams>(raw)));
        return this;
    }

    public RpcHandlerTable AddNotification(string method, Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Register(_notifications, method, _ => handler());
        return this;
    }

    internal bool TryGetRequest(string method, out RpcRequestHandler? handler) => _requests.TryGetValue(method, out handler);

    internal bool TryGetNotification(string method, out RpcNotificationHandler? handler) => _notifications.TryGetValue(method, out handler);

    /// <summary>Called when the connection starts; registering later would race with incoming calls.</summary>
    internal void Freeze() => _frozen = true;

    private void Register<THandler>(Dictionary<string, THandler> table, string method, THandler handler)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        if (RpcMethods.IsReserved(method))
        {
            throw new ArgumentException($"Method names starting with '$/' are reserved: {method}", nameof(method));
        }

        if (_frozen)
        {
            throw new InvalidOperationException("Handlers cannot be added after the connection started.");
        }

        if (!table.TryAdd(method, handler))
        {
            throw new ArgumentException($"Method '{method}' is already registered.", nameof(method));
        }
    }

    private static T Read<T>(JsonElement? raw)
    {
        if (raw is not { } element)
        {
            throw new RpcProtocolException("parameters are missing");
        }

        try
        {
            return element.Deserialize<T>(IpcJson.Options) ?? throw new RpcProtocolException("parameters are null");
        }
        catch (JsonException ex)
        {
            throw new RpcProtocolException("parameters are invalid", ex);
        }
        catch (NotSupportedException ex)
        {
            throw new RpcProtocolException("parameters are invalid", ex);
        }
    }
}
