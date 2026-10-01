// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Images.Udif;

/// <summary>A validated chunk in volume coordinates: where its data lands and where its bytes sit in the file.</summary>
internal readonly record struct UdifChunk(
    UdifChunkType Type,
    long Sector,
    long SectorCount,
    long FileOffset,
    long CompressedLength,
    int Partition)
{
    public long EndSector => Sector + SectorCount;

    public bool HasData => Type is not (UdifChunkType.ZeroFill or UdifChunkType.Ignore);
}
