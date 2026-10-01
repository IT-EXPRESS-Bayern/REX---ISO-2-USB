// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Images.Disk;

/// <summary>Partition type GUIDs that matter for recognising images.</summary>
public static class GptTypes
{
    public static readonly Guid EfiSystem = new("C12A7328-F81F-11D2-BA4B-00A0C93EC93B");

    public static readonly Guid BiosBoot = new("21686148-6449-6E6F-744E-656564454649");

    public static readonly Guid MicrosoftBasicData = new("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");

    public static readonly Guid LinuxFilesystem = new("0FC63DAF-8483-4772-8E79-3D69D8477DE4");

    public static readonly Guid AppleHfsPlus = new("48465300-0000-11AA-AA11-00306543ECAC");

    public static readonly Guid AppleApfs = new("7C3457EF-0000-11AA-AA11-00306543ECAC");

    public static readonly Guid FreeBsdBoot = new("83BD6B9D-7F41-11DC-BE0B-001560B84F0F");

    public static readonly Guid FreeBsdUfs = new("516E7CB6-6ECF-11D6-8FF8-00022D09712B");

    public static readonly Guid FreeBsdZfs = new("516E7CBA-6ECF-11D6-8FF8-00022D09712B");

    public static readonly Guid FreeBsdSwap = new("516E7CB5-6ECF-11D6-8FF8-00022D09712B");

    public static readonly Guid OpenBsdData = new("824CC7A0-36A8-11E3-890A-952519AD3F61");

    public static readonly Guid NetBsdFfs = new("49F48D5A-B10E-11DC-B99B-0019D1879648");

    public static bool IsBsd(Guid type) =>
        type == FreeBsdBoot || type == FreeBsdUfs || type == FreeBsdZfs || type == FreeBsdSwap
        || type == OpenBsdData || type == NetBsdFfs;
}
