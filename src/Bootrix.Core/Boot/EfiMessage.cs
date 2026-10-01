// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Localization;

namespace Bootrix.Core.Boot;

/// <summary>
/// A user-facing statement as resource key plus arguments, so the same report can be rendered in any language
/// and by the CLI as well as the UI.
/// </summary>
public sealed record EfiMessage(string Key, params object?[] Arguments)
{
    public string Format(Localizer? localizer = null) => (localizer ?? Localizer.Default).Get(Key, Arguments);

    // The compiler generated comparison would compare the argument array by reference.
    public bool Equals(EfiMessage? other) =>
        other is not null && Key == other.Key && Arguments.AsSpan().SequenceEqual(other.Arguments);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Key);
        foreach (var argument in Arguments)
        {
            hash.Add(argument);
        }

        return hash.ToHashCode();
    }
}

public static class EfiMessageKeys
{
    public const string NoSignature = "Efi.Finding.NoSignature";
    public const string SignatureInvalid = "Efi.Finding.SignatureInvalid";
    public const string SignedBy = "Efi.Finding.SignedBy";
    public const string OtherSigner = "Efi.Finding.OtherSigner";
    public const string MachineMismatch = "Efi.Finding.MachineMismatch";
    public const string NotEfiApplication = "Efi.Finding.NotEfiApplication";
    public const string Unreadable = "Efi.Finding.Unreadable";

    public const string RevokedDbxHash = "Efi.Revoked.DbxHash";
    public const string RevokedSbat = "Efi.Revoked.Sbat";
    public const string RevokedSvn = "Efi.Revoked.Svn";
    public const string RevokedCertificate = "Efi.Revoked.Certificate";

    public const string AdviceReplaceRevoked = "Efi.Advice.ReplaceRevoked";
    public const string AdviceUpdateSbat = "Efi.Advice.UpdateSbat";
    public const string AdviceUpdateBootManager = "Efi.Advice.UpdateBootManager";
    public const string AdviceWindows2023 = "Efi.Advice.Windows2023";
    public const string AdviceDualSignedLoader = "Efi.Advice.DualSignedLoader";
    public const string AdviceThirdPartyCa = "Efi.Advice.ThirdPartyCa";
    public const string AdviceSecureBootOff = "Efi.Advice.SecureBootOff";
    public const string AdviceEnrollKey = "Efi.Advice.EnrollKey";
    public const string AdviceFixSignature = "Efi.Advice.FixSignature";

    public const string SummaryNoEfiFiles = "Efi.Summary.NoEfiFiles";
    public const string SummaryRevoked = "Efi.Summary.Revoked";
    public const string SummaryNoSignature = "Efi.Summary.NoSignature";
    public const string SummaryInvalidSignature = "Efi.Summary.InvalidSignature";
    public const string SummaryNotMicrosoftSigned = "Efi.Summary.NotMicrosoftSigned";
    public const string SummaryOnly2011 = "Efi.Summary.Only2011";
    public const string SummaryOnly2023 = "Efi.Summary.Only2023";
    public const string SummaryBoth = "Efi.Summary.Both";

    public static string ProfileName(FirmwareProfileId profile) => "Efi.Profile." + profile;

    public static string VerdictName(BootVerdict verdict) => "Efi.Verdict." + verdict;
}
