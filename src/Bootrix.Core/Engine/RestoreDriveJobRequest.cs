// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Model;
using Bootrix.Core.Writing.Restore;

namespace Bootrix.Core.Engine;

/// <summary>
/// Returns sticks to a clean state, for instance after a hybrid image: tables and the start and end of the disk are
/// erased, then one partition over the whole disk is created and formatted.
/// </summary>
public sealed record RestoreDriveJobRequest : EngineJobRequest
{
    public required IReadOnlyList<EngineTarget> Targets { get; init; }

    public PartitionScheme Scheme { get; init; } = PartitionScheme.Mbr;

    /// <summary>FAT32, exFAT or NTFS; automatic chooses by size.</summary>
    public FileSystemKind FileSystem { get; init; } = FileSystemKind.Auto;

    public string? Label { get; init; }

    public int? ClusterSizeBytes { get; init; }

    public RestoreOptions ToOptions() => new()
    {
        Scheme = Scheme,
        FileSystem = FileSystem,
        Label = Label,
        ClusterSizeBytes = ClusterSizeBytes,
    };
}
