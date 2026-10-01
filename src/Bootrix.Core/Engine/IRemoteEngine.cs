// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Engine;

/// <summary>An engine that lives in another process and stops working when the connection to it breaks.</summary>
public interface IRemoteEngine : IEngine
{
    bool IsConnected { get; }

    /// <summary>Completes when the connection ended, whatever the reason.</summary>
    Task Disconnected { get; }
}
