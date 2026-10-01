// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using Bootrix.Core.Errors;
using Microsoft.Extensions.Logging;

namespace Bootrix.Core.Boot;

/// <summary>Analyses a single file: parsing, hash, signatures, revocation checks and the resulting advice.</summary>
internal sealed partial class EfiFileAnalyzer(RevocationData revocations, IReadOnlyList<X509Certificate2> pinnedAuthorities, ILogger logger)
{
    private readonly EfiSignatureReader _signatureReader = new(pinnedAuthorities);

    public static EfiFileRole DetermineRole(string path)
    {
        var normalized = "/" + path.Replace('\\', '/').TrimStart('/');
        if (FallbackLoaderPattern().IsMatch(normalized))
        {
            return EfiFileRole.FallbackLoader;
        }

        return WindowsBootPattern().IsMatch(normalized) ? EfiFileRole.WindowsBootManager : EfiFileRole.Other;
    }

    public EfiFileReport Analyze(string path, Stream data, CancellationToken cancellationToken)
    {
        var role = DetermineRole(path);
        try
        {
            var binary = EfiBinary.Load(data, cancellationToken: cancellationToken);
            var hash = Convert.ToHexString(binary.ComputeAuthenticodeHash(HashAlgorithmName.SHA256, cancellationToken));
            var signatures = _signatureReader.Read(binary, cancellationToken).Select(MarkRevokedCertificate).ToList();
            var sbat = binary.ReadSbat() ?? [];
            var svn = binary.ReadBootmgrSecurityVersion();

            var revocationReasons = FindRevocations(path, hash, signatures, sbat, svn);
            var report = new EfiFileReport(
                path,
                role,
                binary.Size,
                binary.Machine,
                binary.Subsystem,
                hash,
                signatures,
                sbat,
                svn,
                revocationReasons,
                BuildFindings(path, role, binary, signatures),
                BuildAdvice(path, role, signatures, revocationReasons),
                new Dictionary<FirmwareProfileId, BootVerdict>());

            return report with { Verdicts = FirmwareProfile.All.ToDictionary(p => p.Id, p => p.Evaluate(report)) };
        }
        catch (BootrixException ex) when (ex.Code == ErrorCode.EfiBinaryInvalid)
        {
            logger.LogWarning("{Path} is not a usable EFI image: {Reason}", path, ex.Detail);
            return EfiFileReport.Unreadable(path, role, data.CanSeek ? data.Length : 0, ex.Detail ?? ex.Message);
        }
    }

    [GeneratedRegex(@"/efi/boot/boot(ia32|x64|arm|aa64|riscv64|loongarch64)\.efi$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FallbackLoaderPattern();

    [GeneratedRegex(@"/efi/microsoft/boot/[^/]+\.efi$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WindowsBootPattern();

    private static string Authorities(IEnumerable<SignatureAuthority> authorities) =>
        string.Join(", ", authorities.Distinct().Select(SignatureAuthorityInfo.DisplayName));

    private static string Date(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-";

    private EfiSignature MarkRevokedCertificate(EfiSignature signature)
    {
        var revoked = revocations.FindRevokedCertificate(signature.Chain);
        return revoked is null ? signature : signature with { RevokedCertificate = revoked.CommonName };
    }

    private List<RevocationReason> FindRevocations(
        string path,
        string hash,
        List<EfiSignature> signatures,
        IReadOnlyList<SbatEntry> sbat,
        SecurityVersion? svn)
    {
        var reasons = new List<RevocationReason>();

        // An unsigned file is rejected before the DBX is consulted, so only signed files are matched (as Rufus does).
        if (signatures.Count > 0 && revocations.FindImage(hash) is { } image)
        {
            reasons.Add(new RevocationReason(
                RevocationKind.DbxHash,
                new EfiMessage(EfiMessageKeys.RevokedDbxHash, path, hash, Date(image.Added)),
                new EfiMessage(EfiMessageKeys.AdviceReplaceRevoked, path)));
        }

        if (revocations.SbatLevel is { } level)
        {
            foreach (var violation in level.FindViolations(sbat))
            {
                reasons.Add(new RevocationReason(
                    RevocationKind.SbatGeneration,
                    new EfiMessage(EfiMessageKeys.RevokedSbat, path, violation.Component, violation.Generation, violation.Required, level.LevelDate),
                    new EfiMessage(EfiMessageKeys.AdviceUpdateSbat, path, violation.Component, violation.Required)));
            }
        }

        // Only the boot manager's resource name is documented; cdboot.efi and wdsmgfw.efi have SVN entries in the DBX
        // but their resource names are not, so those two are not compared.
        if (svn is { } actual && revocations.FindSvn(RevocationData.BootmgrSvnGuid) is { } required && actual < required.Minimum)
        {
            reasons.Add(new RevocationReason(
                RevocationKind.SecurityVersion,
                new EfiMessage(EfiMessageKeys.RevokedSvn, path, actual, required.Minimum),
                new EfiMessage(EfiMessageKeys.AdviceUpdateBootManager, path, required.Minimum)));
        }

        foreach (var signature in signatures.Where(s => s.RevokedCertificate is not null))
        {
            reasons.Add(new RevocationReason(
                RevocationKind.Certificate,
                new EfiMessage(EfiMessageKeys.RevokedCertificate, path, signature.RevokedCertificate)));
        }

        return reasons;
    }

    private static List<EfiMessage> BuildFindings(string path, EfiFileRole role, EfiBinary binary, List<EfiSignature> signatures)
    {
        var findings = new List<EfiMessage>();
        if (!binary.IsEfiImage)
        {
            findings.Add(new EfiMessage(EfiMessageKeys.NotEfiApplication, path, binary.Subsystem));
        }

        if (signatures.Count == 0)
        {
            findings.Add(new EfiMessage(EfiMessageKeys.NoSignature, path));
        }

        var microsoft = signatures.Where(s => s.IsIntact && s.Authority != SignatureAuthority.Other).Select(s => s.Authority).ToList();
        if (microsoft.Count > 0)
        {
            findings.Add(new EfiMessage(EfiMessageKeys.SignedBy, path, Authorities(microsoft)));
        }

        if (signatures.FirstOrDefault(s => s.IsIntact && s.Authority == SignatureAuthority.Other) is { } other)
        {
            findings.Add(new EfiMessage(EfiMessageKeys.OtherSigner, path, other.Signer?.CommonName ?? "?"));
        }

        if (signatures.Any(s => !s.IsIntact))
        {
            findings.Add(new EfiMessage(EfiMessageKeys.SignatureInvalid, path));
        }

        if (role == EfiFileRole.FallbackLoader)
        {
            var expected = EfiMachineInfo.FallbackSuffix(binary.Machine);
            var name = Path.GetFileNameWithoutExtension(path.Replace('\\', '/'))["boot".Length..];
            if (!string.Equals(expected, name, StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new EfiMessage(EfiMessageKeys.MachineMismatch, path, binary.Machine, name.ToUpperInvariant()));
            }
        }

        return findings;
    }

    private static List<EfiMessage> BuildAdvice(string path, EfiFileRole role, List<EfiSignature> signatures, List<RevocationReason> reasons)
    {
        var advice = reasons.Select(r => r.Advice).OfType<EfiMessage>().ToList();
        if (signatures.Any(s => !s.IsIntact))
        {
            advice.Add(new EfiMessage(EfiMessageKeys.AdviceFixSignature, path));
        }

        var intact = signatures.Where(s => s.IsIntact).Select(s => s.Authority).Distinct().ToList();
        if (intact.Count > 0 && intact.All(a => a == SignatureAuthority.WindowsProductionPca2011))
        {
            advice.Add(new EfiMessage(EfiMessageKeys.AdviceWindows2023, path));
        }
        else if (intact.Count > 0 && intact.All(a => a == SignatureAuthority.MicrosoftUefiCa2011))
        {
            advice.Add(new EfiMessage(EfiMessageKeys.AdviceDualSignedLoader, path));
        }

        // Only the file firmware itself starts needs a trusted signature; later stages are verified by shim or the boot manager.
        if (role == EfiFileRole.FallbackLoader)
        {
            if (signatures.Count == 0)
            {
                advice.Add(new EfiMessage(EfiMessageKeys.AdviceSecureBootOff, path));
            }
            else if (intact.Count > 0 && intact.All(a => a == SignatureAuthority.Other))
            {
                var signer = signatures.First(s => s.IsIntact).Signer?.CommonName ?? "?";
                advice.Add(new EfiMessage(EfiMessageKeys.AdviceEnrollKey, path, signer));
            }
        }

        return advice;
    }
}
