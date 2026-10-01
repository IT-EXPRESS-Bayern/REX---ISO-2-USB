// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Ipc;

/// <summary>What a handler learns about the call it is serving.</summary>
public sealed class RpcCallContext
{
    internal RpcCallContext(RpcConnection connection, long requestId, string method, CancellationToken cancellationToken, CancellationToken abortToken)
    {
        Connection = connection;
        RequestId = requestId;
        Method = method;
        CancellationToken = cancellationToken;
        AbortToken = abortToken;
    }

    /// <summary>The connection the call arrived on, for notifications back to the caller.</summary>
    public RpcConnection Connection { get; }

    public long RequestId { get; }

    public string Method { get; }

    /// <summary>The caller asked to stop at the next safe point. Also set when <see cref="AbortToken"/> is.</summary>
    public CancellationToken CancellationToken { get; }

    /// <summary>The caller wants an immediate stop, or the connection is gone. Cleanup is still expected to run.</summary>
    public CancellationToken AbortToken { get; }
}
