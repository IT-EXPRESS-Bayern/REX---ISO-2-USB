// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Model;

namespace Bootrix.Core.Partitioning;

/// <summary>The partition table of a whole disk, described independent of who writes it.</summary>
public sealed record DiskLayout
{
    /// <summary>Mbr or Gpt; <see cref="PartitionScheme.Auto"/> is not a table format.</summary>
    public required PartitionScheme Scheme { get; init; }

    public required long TotalSectors { get; init; }

    public IReadOnlyList<PartitionEntry> Partitions { get; init; } = [];

    /// <summary>MBR disk signature, unique per disk. Zero is written as is.</summary>
    public uint MbrSignature { get; init; }

    public Guid DiskGuid { get; init; } = Guid.NewGuid();

    /// <summary>Boot code for the MBR (also placed in the protective MBR of a GPT disk); null writes the "Missing operating system" stub.</summary>
    public byte[]? Bootstrap { get; init; }

    public ChsGeometry Geometry { get; init; } = ChsGeometry.Translated;

    /// <summary>GPT only: indices into <see cref="Partitions"/> that a hybrid MBR mirrors, at most three. Empty writes a plain protective MBR.</summary>
    public IReadOnlyList<int> HybridPartitions { get; init; } = [];
}
