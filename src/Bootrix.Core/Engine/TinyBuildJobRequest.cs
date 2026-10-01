// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Tiny;
using Bootrix.Core.Unattend;

namespace Bootrix.Core.Engine;

/// <summary>Builds a Tiny medium from an original Windows ISO. The image is mounted and serviced by the engine.</summary>
public sealed record TinyBuildJobRequest : EngineJobRequest
{
    public required string IsoPath { get; init; }

    public required string OutputIsoPath { get; init; }

    public string ProfileId { get; init; } = "tiny11";

    /// <summary>Edition by index or by part of its name; may be empty when the ISO holds only one.</summary>
    public string? Edition { get; init; }

    /// <summary>Option groups the user wants to leave untouched, e.g. "edge".</summary>
    public IReadOnlyList<string> KeepGroups { get; init; } = [];

    /// <summary>Option groups that are off by default and should be applied, e.g. "tools".</summary>
    public IReadOnlyList<string> IncludeGroups { get; init; } = [];

    /// <summary>Scratch folder; empty uses the engine's work directory.</summary>
    public string? WorkDirectory { get; init; }

    public string VolumeLabel { get; init; } = "TINY";

    public InstallImageCompression Compression { get; init; } = InstallImageCompression.Recovery;

    public bool BypassHardwareChecks { get; init; } = true;

    public UnattendOptions? Unattend { get; init; }

    public bool AcknowledgeNoServicing { get; init; }

    public bool KeepWorkDirectory { get; init; }
}
