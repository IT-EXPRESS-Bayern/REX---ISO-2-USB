// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Workshop;

public enum FirmwareType
{
    Unknown,
    Bios,
    Uefi,
}

public enum SecureBootState
{
    Unknown,
    Off,
    On,
}

public sealed record FirmwareInfo
{
    public FirmwareType Type { get; init; }

    /// <summary>Only meaningful for UEFI; a BIOS machine reports Unknown because Secure Boot does not exist there.</summary>
    public SecureBootState SecureBoot { get; init; }

    public string? Vendor { get; init; }

    public string? Version { get; init; }

    public DateOnly? ReleaseDate { get; init; }

    public string? SmbiosVersion { get; init; }
}

/// <summary>Which boot manager certificates the firmware trusts, found by searching db and dbx; null where a variable could not be read.</summary>
public sealed record SecureBootCertificateInfo
{
    public bool? Windows2023InDb { get; init; }

    public bool? Windows2011InDb { get; init; }

    /// <summary>The Windows Production PCA 2011 is revoked in dbx: only boot managers signed with the 2023 CA start.</summary>
    public bool? Windows2011RevokedInDbx { get; init; }
}
