// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Windows.Storage;

public enum LayoutStyle
{
    Mbr,
    Gpt,
}

public sealed record PartitionSpec
{
    public required long OffsetBytes { get; init; }

    public required long LengthBytes { get; init; }

    public byte MbrType { get; init; }

    public Guid GptType { get; init; }

    public bool Active { get; init; }

    public string Name { get; init; } = "";

    public ulong GptAttributes { get; init; }

    public Guid? GptId { get; init; }
}

/// <summary>Partition table to be applied to a disk through IOCTL_DISK_CREATE_DISK and IOCTL_DISK_SET_DRIVE_LAYOUT_EX.</summary>
public sealed record LayoutSpec
{
    public required LayoutStyle Style { get; init; }

    public required long DiskSizeBytes { get; init; }

    public required int SectorSize { get; init; }

    public required IReadOnlyList<PartitionSpec> Partitions { get; init; }

    public uint MbrSignature { get; init; }

    public Guid GptDiskId { get; init; }
}
