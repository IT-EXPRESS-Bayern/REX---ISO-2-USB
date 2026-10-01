// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;

namespace Bootrix.Core.Images.Udif;

/// <summary>One partition ("blkx" resource) of a UDIF image.</summary>
/// <param name="Id">Resource ID; -1 is the protective MBR of whole-disk images.</param>
/// <param name="Name">Name as stored, usually "description (Type : index)".</param>
/// <param name="Type">The type inside the name, for example Apple_HFS, or null if the name has none.</param>
/// <param name="StartSector">First 512-byte sector inside the decoded volume.</param>
/// <param name="SectorCount">Number of 512-byte sectors.</param>
/// <param name="ChunkCount">Chunks that carry or stand for data (comments and terminators excluded).</param>
/// <param name="Checksum">The checksum of the decoded partition as stored in the block table.</param>
public sealed partial record DmgPartitionInfo(
    int Id,
    string Name,
    string? Type,
    long StartSector,
    long SectorCount,
    int ChunkCount,
    UdifChecksum Checksum)
{
    public long StartOffset => StartSector * DmgReader.SectorSize;

    public long Length => SectorCount * DmgReader.SectorSize;

    internal static string? ExtractType(string name)
    {
        var match = NamePattern().Match(name);
        return match.Success ? match.Groups["type"].Value : null;
    }

    [GeneratedRegex(@"\(\s*(?<type>[^():]+?)\s*:\s*\d+\s*\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
