// SPDX-License-Identifier: GPL-3.0-or-later
using System.Reflection.PortableExecutable;

namespace Bootrix.Core.Boot;

public enum EfiFileRole
{
    Other,

    /// <summary>\EFI\BOOT\BOOT*.EFI, the file firmware starts from removable media.</summary>
    FallbackLoader,

    /// <summary>\EFI\Microsoft\Boot\bootmgfw.efi and its siblings.</summary>
    WindowsBootManager,
}

public enum RevocationKind
{
    /// <summary>The Authenticode hash is listed in the DBX.</summary>
    DbxHash,

    /// <summary>A SBAT generation is below the SbatLevel.</summary>
    SbatGeneration,

    /// <summary>The boot manager's Secure Version Number is below the one in the DBX.</summary>
    SecurityVersion,

    /// <summary>A CA of the signature chain is listed in the DBX.</summary>
    Certificate,
}

/// <param name="Message">The cause and its effect.</param>
/// <param name="Advice">What to do about it; null when the cause has no direct remedy.</param>
public sealed record RevocationReason(RevocationKind Kind, EfiMessage Message, EfiMessage? Advice = null);

/// <summary>The analysis result for one file of the medium.</summary>
/// <param name="AuthenticodeSha256">Upper-case hex; null when the file could not be parsed as a PE image.</param>
/// <param name="Revocations">Why current Secure Boot data blocks the file, one entry per cause.</param>
/// <param name="Findings">Neutral observations: signer, unsigned, mismatching file name, unreadable file.</param>
/// <param name="Recommendations">What to do about the revocations and compatibility gaps.</param>
/// <param name="Verdicts">Outcome per firmware profile, see <see cref="FirmwareProfile"/>.</param>
public sealed record EfiFileReport(
    string Path,
    EfiFileRole Role,
    long Size,
    EfiMachine Machine,
    Subsystem Subsystem,
    string? AuthenticodeSha256,
    IReadOnlyList<EfiSignature> Signatures,
    IReadOnlyList<SbatEntry> Sbat,
    SecurityVersion? BootmgrSecurityVersion,
    IReadOnlyList<RevocationReason> Revocations,
    IReadOnlyList<EfiMessage> Findings,
    IReadOnlyList<EfiMessage> Recommendations,
    IReadOnlyDictionary<FirmwareProfileId, BootVerdict> Verdicts)
{
    public bool IsReadable => AuthenticodeSha256 is not null;

    public bool IsEfiImage => IsReadable && Subsystem is >= Subsystem.EfiApplication and <= Subsystem.EfiRom;

    public bool IsSigned => Signatures.Count > 0;

    /// <summary>
    /// True when the hash, the SBAT generation or the Secure Version Number is rejected by current revocation data.
    /// A revoked CA is deliberately not counted: that DBX entry is an opt-in mitigation, so it only matters for the
    /// firmware profile that has applied it.
    /// </summary>
    public bool IsRevoked => Revocations.Any(r => r.Kind != RevocationKind.Certificate);

    public IReadOnlyList<SignatureAuthority> Authorities =>
        Signatures.Count == 0 ? [SignatureAuthority.NoSignature] : Signatures.Select(s => s.Authority).Distinct().ToList();

    internal static EfiFileReport Unreadable(string path, EfiFileRole role, long size, string reason) => new(
        path,
        role,
        size,
        EfiMachine.Unknown,
        Subsystem.Unknown,
        null,
        [],
        [],
        null,
        [],
        [new EfiMessage(EfiMessageKeys.Unreadable, path, reason)],
        [],
        new Dictionary<FirmwareProfileId, BootVerdict>());
}
