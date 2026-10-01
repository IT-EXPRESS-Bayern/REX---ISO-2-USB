// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Boot;

public enum EfiMediaVerdict
{
    NoEfiFiles,

    /// <summary>An entry point is blocked by the current DBX, SBAT level or SVN.</summary>
    Revoked,
    NoSignature,

    /// <summary>A signature exists but does not match the file.</summary>
    InvalidSignature,

    /// <summary>Signed, but not by a Microsoft Secure Boot CA (or by a CA that no profile trusts).</summary>
    NotMicrosoftSigned,

    /// <summary>Boots on firmware with 2011 CAs but not on firmware that only knows the 2023 CAs.</summary>
    Only2011,

    /// <summary>Boots on firmware with 2023 CAs but not on firmware that only knows the 2011 CAs.</summary>
    Only2023,
    Both2011And2023,
}

/// <param name="Files">Every analysed file, in the order the caller supplied them.</param>
/// <param name="Summary">One statement about the medium as a whole.</param>
/// <param name="Recommendations">Distinct advice of all files plus advice that only makes sense for the whole medium.</param>
/// <param name="RequiresThirdPartyCa">The entry points are signed by a Microsoft third-party CA, so firmware without it cannot boot them.</param>
/// <param name="RevocationSources">Which revocation data the verdicts rest on.</param>
/// <param name="RevocationDataDate">When that data was fetched, if known.</param>
public sealed record EfiAnalysisReport(
    IReadOnlyList<EfiFileReport> Files,
    CompatibilityMatrix Matrix,
    EfiMediaVerdict Verdict,
    IReadOnlyList<EfiMessage> Summary,
    IReadOnlyList<EfiMessage> Recommendations,
    bool RequiresThirdPartyCa,
    IReadOnlyList<RevocationSource> RevocationSources,
    DateOnly? RevocationDataDate)
{
    public bool HasRevokedFiles => Files.Any(f => f.IsRevoked);
}
