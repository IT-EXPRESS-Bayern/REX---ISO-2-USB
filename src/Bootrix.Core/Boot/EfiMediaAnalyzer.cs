// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Core.Boot;

/// <summary>
/// Looks at every EFI binary of a medium and answers two questions: is any of them blocked by current Secure Boot
/// revocation data, and on which classes of firmware will the medium start. The caller enumerates the files (from a
/// file system, an ISO or the El Torito EFI image); this class only sees streams.
/// </summary>
public sealed class EfiMediaAnalyzer
{
    private readonly RevocationData _revocations;
    private readonly ILogger _logger;

    public EfiMediaAnalyzer(RevocationData? revocations = null, ILogger<EfiMediaAnalyzer>? logger = null)
    {
        _revocations = revocations ?? RevocationData.Embedded;
        _logger = logger ?? NullLogger<EfiMediaAnalyzer>.Instance;
    }

    /// <summary>
    /// Analyses the files in order. A file that is not a usable PE image is reported as unreadable and does not stop
    /// the run; cancellation does.
    /// </summary>
    public EfiAnalysisReport Analyze(IEnumerable<(string Path, Stream Data)> files, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);

        // A device DBX carries no CA certificates, so the embedded ones are always available as a fallback.
        var pinned = MicrosoftCertificateAuthorities.LoadPinned(_revocations.TrustedCertificates.Concat(RevocationData.Embedded.TrustedCertificates));
        try
        {
            var analyzer = new EfiFileAnalyzer(_revocations, pinned, _logger);
            var reports = new List<EfiFileReport>();
            foreach (var (path, data) in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                reports.Add(analyzer.Analyze(path, data, cancellationToken));
            }

            return Summarize(reports);
        }
        finally
        {
            foreach (var certificate in pinned)
            {
                certificate.Dispose();
            }
        }
    }

    /// <summary>Builds the media level result from per-file reports, for example ones that were cached or created elsewhere.</summary>
    public EfiAnalysisReport Summarize(IReadOnlyList<EfiFileReport> reports)
    {
        var entries = SelectEntries(reports);
        var matrix = CompatibilityMatrix.Build(entries);
        var revokedFiles = reports.Where(r => r.IsRevoked && r.IsEfiImage).Select(r => r.Path).ToList();
        var verdict = Classify(entries, matrix, revokedFiles);
        var requiresThirdPartyCa = entries.Count > 0 && entries.All(OnlyThirdParty);

        var recommendations = reports.SelectMany(r => r.Recommendations).Distinct().ToList();
        if (requiresThirdPartyCa)
        {
            recommendations.Add(new EfiMessage(EfiMessageKeys.AdviceThirdPartyCa));
        }

        return new EfiAnalysisReport(
            reports,
            matrix,
            verdict,
            [SummaryMessage(verdict, revokedFiles)],
            recommendations,
            requiresThirdPartyCa,
            _revocations.Sources,
            _revocations.Retrieved);
    }

    private static List<EfiFileReport> SelectEntries(IReadOnlyList<EfiFileReport> reports)
    {
        var readable = reports.Where(r => r.IsReadable).ToList();
        foreach (var role in new[] { EfiFileRole.FallbackLoader, EfiFileRole.WindowsBootManager })
        {
            var entries = readable.Where(r => r.Role == role).ToList();
            if (entries.Count > 0)
            {
                return entries;
            }
        }

        return readable.Where(r => r.IsEfiImage).ToList();
    }

    private static bool OnlyThirdParty(EfiFileReport file)
    {
        var intact = file.Signatures.Where(s => s.IsIntact).Select(s => s.Authority).ToList();
        return intact.Count > 0 && intact.All(SignatureAuthorityInfo.IsThirdParty);
    }

    private static EfiMediaVerdict Classify(List<EfiFileReport> entries, CompatibilityMatrix matrix, List<string> revokedFiles)
    {
        if (entries.Count == 0)
        {
            return EfiMediaVerdict.NoEfiFiles;
        }

        // A revoked later stage (grub behind shim, a boot manager next to the fallback loader) stops the boot as well.
        if (revokedFiles.Count > 0 || matrix.AnyRevoked(FirmwareProfileId.Updated2011And2023))
        {
            return EfiMediaVerdict.Revoked;
        }

        if (entries.All(e => !e.IsSigned))
        {
            return EfiMediaVerdict.NoSignature;
        }

        if (entries.Any(e => e.IsSigned && !e.Signatures.Any(s => s.IsIntact)))
        {
            return EfiMediaVerdict.InvalidSignature;
        }

        return (matrix.Boots(FirmwareProfileId.Legacy2011), matrix.Boots(FirmwareProfileId.Only2023)) switch
        {
            (true, true) => EfiMediaVerdict.Both2011And2023,
            (true, false) => EfiMediaVerdict.Only2011,
            (false, true) => EfiMediaVerdict.Only2023,
            _ => EfiMediaVerdict.NotMicrosoftSigned,
        };
    }

    private static EfiMessage SummaryMessage(EfiMediaVerdict verdict, List<string> revokedFiles) => verdict switch
    {
        EfiMediaVerdict.NoEfiFiles => new EfiMessage(EfiMessageKeys.SummaryNoEfiFiles),
        EfiMediaVerdict.Revoked => new EfiMessage(EfiMessageKeys.SummaryRevoked, string.Join(", ", revokedFiles)),
        EfiMediaVerdict.NoSignature => new EfiMessage(EfiMessageKeys.SummaryNoSignature),
        EfiMediaVerdict.InvalidSignature => new EfiMessage(EfiMessageKeys.SummaryInvalidSignature),
        EfiMediaVerdict.Only2011 => new EfiMessage(EfiMessageKeys.SummaryOnly2011),
        EfiMediaVerdict.Only2023 => new EfiMessage(EfiMessageKeys.SummaryOnly2023),
        EfiMediaVerdict.Both2011And2023 => new EfiMessage(EfiMessageKeys.SummaryBoth),
        _ => new EfiMessage(EfiMessageKeys.SummaryNotMicrosoftSigned),
    };
}
