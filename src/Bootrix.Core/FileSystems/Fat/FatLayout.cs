// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.FileSystems.Fat;

/// <summary>The resolved on-disk geometry of a FAT volume; see <see cref="FatGeometry.Compute"/>.</summary>
public sealed record FatLayout
{
    public required FatType Type { get; init; }

    public required int BytesPerSector { get; init; }

    public required int SectorsPerCluster { get; init; }

    public required int ReservedSectors { get; init; }

    public required int FatCount { get; init; }

    /// <summary>Zero on FAT32, where the root directory lives in the cluster chain.</summary>
    public required int RootEntries { get; init; }

    public required long TotalSectors { get; init; }

    public required long SectorsPerFat { get; init; }

    public required long ClusterCount { get; init; }

    public long ClusterBytes => (long)SectorsPerCluster * BytesPerSector;

    public long RootDirectorySectors => ((long)RootEntries * 32 + BytesPerSector - 1) / BytesPerSector;

    public long RootDirectoryStartSector => ReservedSectors + FatCount * SectorsPerFat;

    public long DataStartSector => RootDirectoryStartSector + RootDirectorySectors;

    public long FatBytes => SectorsPerFat * BytesPerSector;
}
