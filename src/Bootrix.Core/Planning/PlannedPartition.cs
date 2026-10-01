// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Model;
using Bootrix.Core.Partitioning;

namespace Bootrix.Core.Planning;

/// <summary>One partition of a <see cref="MediaPlan"/>, in bytes so it applies to formatting as well as to the partition table.</summary>
public sealed record PlannedPartition
{
    public required PartitionRole Role { get; init; }

    public required long StartBytes { get; init; }

    public required long LengthBytes { get; init; }

    /// <summary>Null for partitions that carry no file system (MSR, BIOS boot).</summary>
    public FileSystemKind? FileSystem { get; init; }

    public string? Label { get; init; }

    /// <summary>The MBR boot flag.</summary>
    public bool Active { get; init; }

    /// <summary>Partition type in an MBR layout; zero where MBR has no equivalent.</summary>
    public byte MbrType { get; init; }

    /// <summary>Partition type in a GPT layout.</summary>
    public Guid GptType { get; init; }

    public GptAttributes GptAttributes { get; init; }

    /// <summary>Null leaves the choice to the formatter.</summary>
    public int? ClusterSizeBytes { get; init; }

    public long EndBytes => StartBytes + LengthBytes;

    public long StartLba(int sectorSize) => StartBytes / sectorSize;

    public long SectorCount(int sectorSize) => LengthBytes / sectorSize;

    public PartitionEntry ToEntry(int sectorSize, Guid? uniqueId = null) => new()
    {
        StartLba = StartLba(sectorSize),
        SectorCount = SectorCount(sectorSize),
        MbrType = MbrType,
        GptType = GptType,
        UniqueId = uniqueId,
        Name = Label ?? "",
        Active = Active,
        Attributes = GptAttributes,
    };
}
