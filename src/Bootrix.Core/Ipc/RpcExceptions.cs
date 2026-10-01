// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;

namespace Bootrix.Core.Ipc;

/// <summary>
/// The other side sent something that is not part of the protocol: a bad frame, an unknown method,
/// parameters of the wrong shape. It derives from <see cref="BootrixException"/> so that the error
/// catalog can describe it without any translation step.
/// </summary>
public class RpcProtocolException : BootrixException
{
    public RpcProtocolException(string reason, Exception? inner = null)
        : base(ErrorCode.BrokerProtocol, reason, inner)
    {
        Arguments = [reason];
    }
}

/// <summary>Raised on every call that was open, or is started, after the connection ended.</summary>
public sealed class RpcConnectionClosedException : BootrixException
{
    public RpcConnectionClosedException(string reason, Exception? inner = null)
        : base(ErrorCode.BrokerDisconnected, reason, inner)
    {
    }
}

/// <summary>A protocol failure that also ends the connection, such as a rejected handshake.</summary>
internal sealed class RpcFatalException(string reason) : RpcProtocolException(reason);
