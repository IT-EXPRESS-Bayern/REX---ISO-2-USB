// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Engine;
using Bootrix.Windows.Broker;
using Bootrix.Windows.Platform;
using Microsoft.Extensions.Logging;

namespace Bootrix.Windows.Engine;

/// <summary>
/// Decides how the engine is reached. A process that already runs elevated, such as the CLI, uses the
/// <see cref="LocalEngine"/> directly. An ordinary one gets an engine that lists disks itself, which needs no rights, and starts
/// the elevated broker the first time a fingerprint or a write is asked for: that is the moment the UAC prompt appears,
/// and not when the program starts or a disk list is shown.
/// </summary>
public sealed class EngineProvider : IAsyncDisposable
{
    private readonly DeferredEngine? _deferred;

    public EngineProvider(LocalEngine local, BrokerLauncher launcher, ILogger<EngineProvider>? logger = null)
        : this(local, async token => await launcher.LaunchAsync(token).ConfigureAwait(false), ProcessElevation.IsElevated(), logger)
    {
    }

    internal EngineProvider(IEngine local, Func<CancellationToken, Task<IEngine>> startBroker, bool elevated, ILogger? logger = null)
    {
        RunsElevated = elevated;
        _deferred = elevated ? null : new DeferredEngine(local, startBroker, logger);
        Engine = _deferred ?? local;
    }

    /// <summary>The engine to use. Safe to call from any thread; nothing privileged starts before it is needed.</summary>
    public IEngine Engine { get; }

    /// <summary>True when this process has administrator rights itself and no broker is involved.</summary>
    public bool RunsElevated { get; }

    /// <summary>Ends the broker, if one was started. The local engine belongs to whoever created it.</summary>
    public ValueTask DisposeAsync() => _deferred?.DisposeAsync() ?? ValueTask.CompletedTask;
}
