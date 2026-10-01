// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Partitioning;

/// <summary>
/// Well-known partition type GUIDs. Written to disk the first three fields are little-endian and
/// the rest in order, which is exactly what <see cref="Guid.TryWriteBytes(Span{byte})"/> produces.
/// </summary>
public static class GptTypes
{
    public static Guid Unused { get; } = Guid.Empty;

    // Windows and UEFI
    public static Guid EfiSystem { get; } = new("C12A7328-F81F-11D2-BA4B-00A0C93EC93B");

    public static Guid MicrosoftReserved { get; } = new("E3C9E316-0B5C-4DB8-817D-F92DF00215AE");

    /// <summary>The only type Windows gives drive letters and volume paths to.</summary>
    public static Guid BasicData { get; } = new("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");

    public static Guid WindowsRecovery { get; } = new("DE94BBA4-06D1-4D40-A16A-BFD50179D6AC");

    public static Guid LdmMetadata { get; } = new("5808C8AA-7E8F-42E0-85D2-E1E90434CFB3");

    public static Guid LdmData { get; } = new("AF9B60A0-1431-4F62-BC68-3311714A69AD");

    public static Guid LegacyMbr { get; } = new("024DEE41-33E7-11D3-9D69-0008C781F39F");

    // Linux and GRUB
    public static Guid LinuxData { get; } = new("0FC63DAF-8483-4772-8E79-3D69D8477DE4");

    /// <summary>GRUB's embedding area on a GPT disk ("Hah!IdontNeedEFI"), where an MBR disk has the gap before the first partition.</summary>
    public static Guid BiosBoot { get; } = new("21686148-6449-6E6F-744E-656564454649");

    public static Guid LinuxRootX64 { get; } = new("4F68BCE3-E8CD-4DB1-96E7-FBCAF984B709");

    public static Guid LinuxHome { get; } = new("933AC7E1-2EB4-4F13-B844-0E14E2AEF915");

    public static Guid LinuxSwap { get; } = new("0657FD6D-A4AB-43C4-84E5-0933C84B4F4F");

    public static Guid LinuxLvm { get; } = new("E6D6D379-F507-44C2-A23C-238F2A3DF928");

    public static Guid LinuxLuks { get; } = new("CA7D7CCB-63ED-4C53-861C-1742536059CC");

    public static Guid ExtendedBootLoader { get; } = new("BC13C2FF-59E6-4262-A352-B275FD6F7172");

    // Apple
    public static Guid AppleHfsPlus { get; } = new("48465300-0000-11AA-AA11-00306543ECAC");

    public static Guid AppleApfs { get; } = new("7C3457EF-0000-11AA-AA11-00306543ECAC");

    public static Guid AppleBoot { get; } = new("426F6F74-0000-11AA-AA11-00306543ECAC");

    public static Guid AppleCoreStorage { get; } = new("53746F72-6167-11AA-AA11-00306543ECAC");

    public static Guid AppleApfsPreboot { get; } = new("69646961-6700-11AA-AA11-00306543ECAC");

    public static Guid AppleApfsRecovery { get; } = new("52637672-7900-11AA-AA11-00306543ECAC");

    public static Guid AppleUfs { get; } = new("55465300-0000-11AA-AA11-00306543ECAC");

    public static Guid AppleRaid { get; } = new("52414944-0000-11AA-AA11-00306543ECAC");

    // BSD
    public static Guid FreeBsdBoot { get; } = new("83BD6B9D-7F41-11DC-BE0B-001560B84F0F");
}
