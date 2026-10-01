// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Images.Udif;

/// <summary>Description of an opened UDIF image.</summary>
public sealed record DmgInfo
{
    /// <summary>Size of the decoded volume in bytes.</summary>
    public required long VolumeSize { get; init; }

    public required long SectorCount { get; init; }

    /// <summary>1 for whole-device images, 2 for single-partition images, as stored in the trailer.</summary>
    public required uint ImageVariant { get; init; }

    public required uint Flags { get; init; }

    /// <summary>True for old images whose trailer sits in front of the data fork.</summary>
    public required bool TrailerAtFront { get; init; }

    /// <summary>True if the block table came from the binary resource fork instead of an XML property list.</summary>
    public required bool UsesResourceFork { get; init; }

    public required IReadOnlyList<DmgPartitionInfo> Partitions { get; init; }

    /// <summary>The distinct chunk types that occur in the image, in numeric order.</summary>
    public required IReadOnlyList<UdifChunkType> ChunkTypes { get; init; }

    public required long ChunkCount { get; init; }

    /// <summary>Size of the data fork, i.e. roughly the size of the compressed payload.</summary>
    public required long DataForkLength { get; init; }
}
