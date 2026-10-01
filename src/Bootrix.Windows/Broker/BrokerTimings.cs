// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Ipc;

namespace Bootrix.Windows.Broker;

/// <summary>The time limits both ends of the broker connection agree on.</summary>
internal static class BrokerTimings
{
    /// <summary>How long the GUI waits for the broker to show up on its pipe after the UAC prompt was confirmed.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long the broker waits for a client, before the first one and after the last one left.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

    /// <summary>A job that is still running this long after an abort is given up on by ending the whole process.</summary>
    public static readonly TimeSpan AbortGrace = TimeSpan.FromSeconds(30);

    /// <summary>The same on both sides. The timeout is generous: a debugger or a heavily loaded machine must not cut a write short.</summary>
    public static RpcConnectionOptions Connection { get; } = new()
    {
        HeartbeatInterval = TimeSpan.FromSeconds(5),
        HeartbeatTimeout = TimeSpan.FromSeconds(60),
    };
}
