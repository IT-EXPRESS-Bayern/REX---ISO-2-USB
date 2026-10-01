// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Boot;

public enum FirmwareProfileId
{
    /// <summary>Firmware that was never updated: db holds only the 2011 CAs and the DBX is old.</summary>
    Legacy2011,

    /// <summary>Updated firmware with the 2011 and the 2023 CAs side by side.</summary>
    Updated2011And2023,

    /// <summary>Firmware whose db holds only 2023 CAs, but still the third-party ones.</summary>
    Only2023,

    /// <summary>Secured-core or default Windows 11 25H2 configuration: Windows UEFI CA 2023 only, no third-party CA.</summary>
    SecuredCore,

    /// <summary>Updated firmware that additionally applied the CVE-2023-24932 mitigation (Windows Production PCA 2011 in the DBX).</summary>
    Pca2011Revoked,
}

public enum BootVerdict
{
    Boots,

    /// <summary>No signature; only starts with Secure Boot switched off.</summary>
    NoSignature,

    /// <summary>The CA behind the signature is not in the db of this profile.</summary>
    SignerNotInDb,

    /// <summary>The signature does not belong to this file or is damaged.</summary>
    SignatureInvalid,

    /// <summary>The firmware's DBX, SBAT level or SVN rejects the file.</summary>
    Revoked,
}

/// <summary>
/// A class of firmware, described by the CAs in its db. What decides whether a third-party loader boots is the
/// presence of a Microsoft UEFI CA in the db, not the year of the CA: the default Windows 11 25H2 db holds only
/// "Windows UEFI CA 2023" and rejects every shim, while the Linux-capable configuration adds the third-party CAs
/// (Microsoft Learn, "Windows Secure Boot key creation and management guidance").
/// </summary>
public sealed record FirmwareProfile(
    FirmwareProfileId Id,
    IReadOnlySet<SignatureAuthority> TrustedAuthorities,
    IReadOnlySet<SignatureAuthority> RevokedAuthorities,
    bool AppliesCurrentRevocations)
{
    public static IReadOnlyList<FirmwareProfile> All { get; } =
    [
        new(
            FirmwareProfileId.Legacy2011,
            new HashSet<SignatureAuthority> { SignatureAuthority.WindowsProductionPca2011, SignatureAuthority.MicrosoftUefiCa2011 },
            new HashSet<SignatureAuthority>(),
            false),
        new(
            FirmwareProfileId.Updated2011And2023,
            AllMicrosoft(),
            new HashSet<SignatureAuthority>(),
            true),
        new(
            FirmwareProfileId.Only2023,
            new HashSet<SignatureAuthority>
            {
                SignatureAuthority.WindowsUefiCa2023,
                SignatureAuthority.MicrosoftUefiCa2023,
                SignatureAuthority.MicrosoftOptionRomUefiCa2023,
            },
            new HashSet<SignatureAuthority>(),
            true),
        new(
            FirmwareProfileId.SecuredCore,
            new HashSet<SignatureAuthority> { SignatureAuthority.WindowsUefiCa2023 },
            new HashSet<SignatureAuthority>(),
            true),
        new(
            FirmwareProfileId.Pca2011Revoked,
            AllMicrosoft(),
            new HashSet<SignatureAuthority> { SignatureAuthority.WindowsProductionPca2011 },
            true),
    ];

    public BootVerdict Evaluate(EfiFileReport file)
    {
        ArgumentNullException.ThrowIfNull(file);

        if (file.Signatures.Count == 0)
        {
            return BootVerdict.NoSignature;
        }

        if (AppliesCurrentRevocations && file.IsRevoked)
        {
            return BootVerdict.Revoked;
        }

        var intact = file.Signatures.Where(s => s.IsIntact).ToList();
        if (intact.Count == 0)
        {
            return BootVerdict.SignatureInvalid;
        }

        var trusted = intact.Where(s => TrustedAuthorities.Contains(s.Authority)).ToList();
        if (trusted.Count == 0)
        {
            return BootVerdict.SignerNotInDb;
        }

        return trusted.All(s => RevokedAuthorities.Contains(s.Authority)) ? BootVerdict.Revoked : BootVerdict.Boots;
    }

    private static HashSet<SignatureAuthority> AllMicrosoft() =>
    [
        SignatureAuthority.WindowsProductionPca2011,
        SignatureAuthority.MicrosoftUefiCa2011,
        SignatureAuthority.WindowsUefiCa2023,
        SignatureAuthority.MicrosoftUefiCa2023,
        SignatureAuthority.MicrosoftOptionRomUefiCa2023,
    ];
}
