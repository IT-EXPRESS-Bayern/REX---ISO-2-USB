// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json.Serialization;
using Bootrix.Core.Storage;

namespace Bootrix.Core.Engine;

/// <summary>
/// A job as it crosses the process boundary. Requests carry only plain data (paths, identities,
/// options); the engine resolves devices and opens files itself, so nothing privileged is ever
/// handed over by reference. Every new kind of job adds one derived type here.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RawWriteJobRequest), "raw-write")]
public abstract record EngineJobRequest;

/// <summary>A disk the user confirmed, together with the fingerprint taken at that moment.</summary>
public sealed record EngineTarget(string DevicePath, DiskIdentity Identity);

public sealed record RawWriteJobRequest : EngineJobRequest
{
    public required string ImagePath { get; init; }

    public required IReadOnlyList<EngineTarget> Targets { get; init; }

    public bool Verify { get; init; } = true;
}
