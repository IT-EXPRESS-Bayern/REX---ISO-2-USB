// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Ipc;

public sealed record RpcConnectionOptions
{
    public const int DefaultMaxFrameBytes = 16 * 1024 * 1024;

    /// <summary>Largest frame either side may send; a bigger one ends the connection.</summary>
    public int MaxFrameBytes { get; init; } = DefaultMaxFrameBytes;

    /// <summary>When set, a ping is sent at this interval and the connection is closed after <see cref="HeartbeatTimeout"/> of silence.</summary>
    public TimeSpan? HeartbeatInterval { get; init; }

    /// <summary>Any received frame counts as a sign of life, not only pings.</summary>
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}
