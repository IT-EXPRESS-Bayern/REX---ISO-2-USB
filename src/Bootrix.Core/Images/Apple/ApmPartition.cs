// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Images.Apple;

/// <summary>Well-known partition type strings of the Apple Partition Map.</summary>
public static class ApmPartitionTypes
{
    public const string PartitionMap = "Apple_partition_map";
    public const string Hfs = "Apple_HFS";
    public const string HfsX = "Apple_HFSX";
    public const string Apfs = "Apple_APFS";
    public const string Ufs = "Apple_UFS";
    public const string Free = "Apple_Free";
    public const string Boot = "Apple_Boot";
    public const string Driver = "Apple_Driver";
    public const string Driver43 = "Apple_Driver43";

    public static bool IsApple(string type) => type.StartsWith("Apple_", StringComparison.OrdinalIgnoreCase);

    /// <summary>Entries that only describe the map itself or hold drivers and free space, not user data.</summary>
    public static bool IsStructural(string type) =>
        type.Equals(PartitionMap, StringComparison.OrdinalIgnoreCase)
        || type.Equals(Free, StringComparison.OrdinalIgnoreCase)
        || type.StartsWith("Apple_Driver", StringComparison.OrdinalIgnoreCase)
        || type.StartsWith("Apple_Patches", StringComparison.OrdinalIgnoreCase)
        || type.Equals("Apple_Void", StringComparison.OrdinalIgnoreCase);
}

/// <summary>One partition map entry. Offsets and lengths are in bytes.</summary>
public sealed record ApmPartition(
    int Index,
    string Name,
    string Type,
    long StartBlock,
    long BlockCount,
    long StartOffset,
    long Length,
    uint Status);
