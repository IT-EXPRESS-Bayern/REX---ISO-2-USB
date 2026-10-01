// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Catalog.Microsoft;

public enum CabSignatureStatus
{
    /// <summary>No verifier was available on this platform.</summary>
    NotChecked,

    /// <summary>The cabinet carries no Authenticode signature.</summary>
    NotSigned,

    Valid,

    /// <summary>A signature is present and does not match the content or does not chain to a trusted root.</summary>
    Invalid,
}

public sealed record CabSignatureResult(CabSignatureStatus Status, string? Signer = null);

/// <summary>
/// Checks the Authenticode signature of a cabinet. Chain building against the Windows trust store is a platform
/// service, so the Windows project supplies the real implementation; the core only defines the contract.
/// </summary>
public interface ICabSignatureVerifier
{
    CabSignatureResult Verify(ReadOnlyMemory<byte> cabinet);
}
