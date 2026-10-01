// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Images.Apple;

/// <summary>GPT partition type GUIDs used by Apple, plus the EFI System Partition that Mac boot media carry as well.</summary>
internal static class AppleGptTypes
{
    public static readonly Guid HfsPlus = new("48465300-0000-11AA-AA11-00306543ECAC");
    public static readonly Guid Apfs = new("7C3457EF-0000-11AA-AA11-00306543ECAC");
    public static readonly Guid EfiSystem = new("C12A7328-F81F-11D2-BA4B-00A0C93EC93B");

    private static readonly Dictionary<Guid, string> Labels = new()
    {
        [HfsPlus] = "Apple HFS+",
        [Apfs] = "Apple APFS",
        [new Guid("426F6F74-0000-11AA-AA11-00306543ECAC")] = "Apple Boot",
        [new Guid("52414944-0000-11AA-AA11-00306543ECAC")] = "Apple RAID",
        [new Guid("52414944-5F4F-11AA-AA11-00306543ECAC")] = "Apple RAID offline",
        [new Guid("55465300-0000-11AA-AA11-00306543ECAC")] = "Apple UFS",
        [new Guid("53746F72-6167-11AA-AA11-00306543ECAC")] = "Apple Core Storage",
        [new Guid("4C616265-6C00-11AA-AA11-00306543ECAC")] = "Apple Label",
        [new Guid("5265636F-7665-11AA-AA11-00306543ECAC")] = "Apple TV Recovery",
        [new Guid("69646961-6700-11AA-AA11-00306543ECAC")] = "APFS Preboot",
        [new Guid("52637672-7900-11AA-AA11-00306543ECAC")] = "APFS Recovery",
        [EfiSystem] = "EFI System",
    };

    /// <summary>Partitions that exist only to start a Mac: recovery, boot support and the EFI System Partition.</summary>
    private static readonly HashSet<Guid> BootSupport =
    [
        new Guid("426F6F74-0000-11AA-AA11-00306543ECAC"),
        new Guid("69646961-6700-11AA-AA11-00306543ECAC"),
        new Guid("52637672-7900-11AA-AA11-00306543ECAC"),
        new Guid("5265636F-7665-11AA-AA11-00306543ECAC"),
    ];

    public static bool IsApple(Guid type) => Labels.ContainsKey(type) && type != EfiSystem;

    public static bool IsBootSupport(Guid type) => BootSupport.Contains(type);

    public static string Describe(Guid type) =>
        Labels.TryGetValue(type, out var label) ? label : type.ToString("D").ToUpperInvariant();
}
