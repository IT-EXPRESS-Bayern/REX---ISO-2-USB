// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Storage.Testing;

namespace Bootrix.Core.Engine;

/// <summary>Tests whether sticks have the capacity they claim and are free of bad blocks. Destroys everything on them.</summary>
public sealed record StickTestJobRequest : EngineJobRequest
{
    public required IReadOnlyList<EngineTarget> Targets { get; init; }

    public StickTestMode Mode { get; init; } = StickTestMode.Capacity;
}
