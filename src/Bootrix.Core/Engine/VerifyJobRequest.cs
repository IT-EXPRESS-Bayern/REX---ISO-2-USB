// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Writing.Raw;
using Bootrix.Core.Writing.Verify;

namespace Bootrix.Core.Engine;

/// <summary>Reads a medium and compares it with the image it was made from; nothing on the medium is changed.</summary>
public sealed record VerifyJobRequest : EngineJobRequest
{
    public required string ImagePath { get; init; }

    public required IReadOnlyList<EngineTarget> Targets { get; init; }

    public VerifyMode Mode { get; init; } = VerifyMode.Auto;

    /// <summary>The file inside a zip archive that holds the image; the archive's image when null.</summary>
    public string? ArchiveEntry { get; init; }

    /// <summary>Raw comparison only: with a block map next to the image only its blocks are compared.</summary>
    public BlockMapUse BlockMap { get; init; } = BlockMapUse.Auto;
}
