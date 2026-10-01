// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Boot;

/// <summary>The CA that vouches for the signer of a boot binary, as far as firmware db contents are concerned.</summary>
public enum SignatureAuthority
{
    /// <summary>The image carries no signature.</summary>
    NoSignature,

    /// <summary>Signed by a chain that does not end in one of the Microsoft Secure Boot CAs.</summary>
    Other,
    WindowsProductionPca2011,
    WindowsUefiCa2023,
    MicrosoftUefiCa2011,
    MicrosoftUefiCa2023,
    MicrosoftOptionRomUefiCa2023,
}

public static class SignatureAuthorityInfo
{
    /// <summary>The certificate subject names Microsoft uses; they are proper names and are not translated.</summary>
    public static string DisplayName(SignatureAuthority authority) => authority switch
    {
        SignatureAuthority.NoSignature => "unsigned",
        SignatureAuthority.WindowsProductionPca2011 => "Microsoft Windows Production PCA 2011",
        SignatureAuthority.WindowsUefiCa2023 => "Windows UEFI CA 2023",
        SignatureAuthority.MicrosoftUefiCa2011 => "Microsoft Corporation UEFI CA 2011",
        SignatureAuthority.MicrosoftUefiCa2023 => "Microsoft UEFI CA 2023",
        SignatureAuthority.MicrosoftOptionRomUefiCa2023 => "Microsoft Option ROM UEFI CA 2023",
        _ => "other",
    };

    /// <summary>True for the two third-party UEFI CAs that sign shim and other non-Windows loaders.</summary>
    public static bool IsThirdParty(SignatureAuthority authority) =>
        authority is SignatureAuthority.MicrosoftUefiCa2011 or SignatureAuthority.MicrosoftUefiCa2023;

    public static bool Is2011(SignatureAuthority authority) =>
        authority is SignatureAuthority.WindowsProductionPca2011 or SignatureAuthority.MicrosoftUefiCa2011;

    public static bool Is2023(SignatureAuthority authority) =>
        authority is SignatureAuthority.WindowsUefiCa2023
            or SignatureAuthority.MicrosoftUefiCa2023
            or SignatureAuthority.MicrosoftOptionRomUefiCa2023;
}
