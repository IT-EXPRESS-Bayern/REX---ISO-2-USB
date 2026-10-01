// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Workshop;

/// <summary>Measures the PC the program runs on. It reads local sources only: no network access and no telemetry.</summary>
public interface ITargetPcCollector
{
    /// <summary>
    /// Every source is read on its own; one that fails leaves its section null and adds a <see cref="CollectionIssue"/>.
    /// Only cancellation makes the call throw.
    /// </summary>
    Task<TargetPcInfo> CollectAsync(CancellationToken cancellationToken = default);
}
