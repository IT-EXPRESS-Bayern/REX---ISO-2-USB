// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bootrix.Core.Boot;
using Bootrix.Core.Errors;
using Bootrix.Core.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Core.Writing.Windows.Customization;

/// <param name="Applied">Whether the medium now carries the 2023-signed files.</param>
/// <param name="Replaced">The files that were replaced, relative to the medium root.</param>
/// <param name="Verdict">The analyser's verdict for the medium after the swap.</param>
/// <param name="SkippedReason">Why nothing was done, in the language of the log; null when the swap was made.</param>
public sealed record BootManagerSwapResult(bool Applied, IReadOnlyList<string> Replaced, EfiMediaVerdict? Verdict, string? SkippedReason);

/// <summary>
/// Replaces the boot manager, the boot applications and the boot fonts of a Windows medium with the versions signed by
/// the Windows UEFI CA 2023, which the Setup image of recent builds carries in <c>Windows\Boot\EFI_EX</c> and
/// <c>Windows\Boot\Fonts_EX</c> (what <c>bcdboot /bootex</c> does for a disk). The signature is not assumed: after
/// the swap the EFI analyser has to confirm that every replaced boot loader carries an intact signature of that CA and
/// is not revoked, otherwise the original files are put back.
/// </summary>
public sealed class Ca2023BootManagerSwap
{
    private const string TemporarySuffix = ".bootrix-new";

    private readonly IBootFileExtractor _extractor;
    private readonly Func<IReadOnlyList<(string Path, Stream Data)>, CancellationToken, EfiAnalysisReport> _analyze;
    private readonly ILogger _logger;

    public Ca2023BootManagerSwap(
        IBootFileExtractor extractor,
        Func<IReadOnlyList<(string Path, Stream Data)>, CancellationToken, EfiAnalysisReport>? analyze = null,
        ILogger? logger = null)
    {
        _extractor = extractor;
        _analyze = analyze ?? ((files, token) => new EfiMediaAnalyzer().Analyze(files, token));
        _logger = logger ?? NullLogger.Instance;
    }

    /// <exception cref="BootrixException">
    /// In <see cref="Ca2023Mode.Required"/> mode with <see cref="ErrorCode.BootManager2023Unavailable"/> when the image has no such files
    /// and with <see cref="ErrorCode.BootManager2023Unverified"/> when the result is not signed as expected.
    /// </exception>
    public async Task<BootManagerSwapResult> ApplyAsync(
        string mediaRoot,
        string workDirectory,
        Ca2023Mode mode,
        int build,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(mediaRoot);
        ArgumentException.ThrowIfNullOrEmpty(workDirectory);
        ArgumentNullException.ThrowIfNull(progress);
        if (mode == Ca2023Mode.Off)
        {
            return new BootManagerSwapResult(false, [], null, "not requested");
        }

        if (MediaPaths.Resolve(mediaRoot, "sources", "boot.wim") is not { } bootImageRelative)
        {
            return Unavailable(mode, build, "Boot2023.Reason.NoBootImage");
        }

        // Targets of one job may be customized side by side, so every call works in a folder of its own.
        var scratch = Path.Combine(workDirectory, "ca2023-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);
        try
        {
            return await SwapAsync(mediaRoot, Path.Combine(mediaRoot, bootImageRelative), scratch, mode, build, new StagedProgress(progress, 4, 1, 1, 2), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteDirectory(scratch);
        }
    }

    private async Task<BootManagerSwapResult> SwapAsync(
        string mediaRoot,
        string bootImage,
        string scratch,
        Ca2023Mode mode,
        int build,
        StagedProgress stages,
        CancellationToken cancellationToken)
    {
        var extracted = Path.Combine(scratch, "extracted");
        Directory.CreateDirectory(extracted);
        try
        {
            await _extractor.ExtractAsync(
                bootImage,
                BootImageIndex.FindSetup(bootImage) ?? 1,
                [$"Windows/Boot/{BootManagerSwapPlan.EfiFolder}", $"Windows/Boot/{BootManagerSwapPlan.FontFolder}"],
                extracted,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (mode == Ca2023Mode.BestEffort && ex is (BootrixException or IOException))
        {
            // The new boot files are an improvement the job did not insist on; a damaged boot.wim is no reason to stop it.
            return Unavailable(mode, build, "Boot2023.Reason.ExtractFailed", ex.Message);
        }

        stages.Complete(0);

        var plan = BootManagerSwapPlan.Create(extracted, mediaRoot);
        stages.Complete(1);
        if (!plan.IsPossible)
        {
            return Unavailable(mode, build, plan.UnavailableReason!, plan.UnavailableDetail);
        }

        var backupRoot = Path.Combine(scratch, "backup");
        var backups = new List<(string Target, string Backup)>();
        EfiAnalysisReport report;
        List<string> problems;
        try
        {
            foreach (var replacement in plan.Replacements)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Replace(replacement, mediaRoot, backupRoot, backups);
            }

            stages.Complete(2);
            report = Analyze(mediaRoot, plan, cancellationToken);
            problems = Evaluate(report, plan);
        }
        catch
        {
            Restore(backups);
            throw;
        }

        if (problems.Count > 0)
        {
            Restore(backups);
            return Unverified(mode, string.Join("; ", problems));
        }

        stages.Complete(3);
        var replaced = plan.Replacements.Select(r => r.TargetRelativePath).ToList();
        _logger.LogInformation("Replaced {Count} boot files with their Windows UEFI CA 2023 versions (verdict {Verdict})", replaced.Count, report.Verdict);
        return new BootManagerSwapResult(true, replaced, report.Verdict, null);
    }

    private static void Replace(BootFileReplacement replacement, string mediaRoot, string backupRoot, List<(string Target, string Backup)> backups)
    {
        var target = Path.Combine(mediaRoot, replacement.TargetRelativePath);
        var backup = Path.Combine(backupRoot, replacement.TargetRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        File.Copy(target, backup, overwrite: true);
        backups.Add((target, backup));

        // The new file is complete under its temporary name before it takes the place of the old one.
        var temporary = target + TemporarySuffix;
        File.Copy(replacement.SourcePath, temporary, overwrite: true);
        File.SetAttributes(temporary, FileAttributes.Normal);
        File.SetAttributes(target, FileAttributes.Normal);
        File.Move(temporary, target, overwrite: true);
    }

    private static void Restore(List<(string Target, string Backup)> backups)
    {
        foreach (var (target, backup) in backups)
        {
            File.SetAttributes(target, FileAttributes.Normal);
            File.Copy(backup, target, overwrite: true);
            TryDelete(target + TemporarySuffix);
        }
    }

    /// <summary>Runs the EFI analyser over the replaced loaders and every fallback loader, labelled with their path on the medium.</summary>
    private EfiAnalysisReport Analyze(string mediaRoot, BootManagerSwapPlan plan, CancellationToken cancellationToken)
    {
        var relatives = plan.Replacements.Where(r => r.IsEfiBinary).Select(r => r.TargetRelativePath).ToList();
        if (MediaPaths.ResolveDirectory(mediaRoot, "efi", "boot") is { } fallbackFolder)
        {
            relatives.AddRange(
                Directory.EnumerateFiles(Path.Combine(mediaRoot, fallbackFolder), "*.efi")
                    .Select(file => Path.GetRelativePath(mediaRoot, file)));
        }

        var streams = new List<(string Path, Stream Data)>();
        try
        {
            foreach (var relative in relatives.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                streams.Add((Label(relative), File.OpenRead(Path.Combine(mediaRoot, relative))));
            }

            return _analyze(streams, cancellationToken);
        }
        finally
        {
            foreach (var (_, data) in streams)
            {
                data.Dispose();
            }
        }
    }

    /// <summary>What has to hold for the swap to stand: every replaced loader readable, intact, signed by the 2023 CA and not revoked.</summary>
    private static List<string> Evaluate(EfiAnalysisReport report, BootManagerSwapPlan plan)
    {
        var problems = new List<string>();
        foreach (var replacement in plan.Replacements.Where(r => r.IsEfiBinary))
        {
            var label = Label(replacement.TargetRelativePath);
            var file = report.Files.FirstOrDefault(f => string.Equals(f.Path, label, StringComparison.OrdinalIgnoreCase));
            if (file is null || !file.IsReadable)
            {
                problems.Add($"{label}: not a valid EFI file");
            }
            else if (!file.Signatures.Any(s => s.IsIntact && s.Authority == SignatureAuthority.WindowsUefiCa2023))
            {
                problems.Add($"{label}: signed by {string.Join(" and ", file.Authorities.Select(SignatureAuthorityInfo.DisplayName))}");
            }
            else if (file.IsRevoked)
            {
                problems.Add($"{label}: revoked");
            }
        }

        if (problems.Count == 0 && report.Verdict is not (EfiMediaVerdict.Only2023 or EfiMediaVerdict.Both2011And2023))
        {
            problems.Add($"medium verdict {report.Verdict}");
        }

        return problems;
    }

    private BootManagerSwapResult Unavailable(Ca2023Mode mode, int build, string reasonKey, string? detail = null)
    {
        var reason = Localizer.Default.Get(reasonKey, detail);
        if (mode == Ca2023Mode.Required)
        {
            throw new BootrixException(ErrorCode.BootManager2023Unavailable, reasonKey)
            {
                Arguments = [build > 0 ? build.ToString(CultureInfo.InvariantCulture) : "?", reason],
            };
        }

        _logger.LogWarning("The medium keeps its boot files; no 2023 versions are available: {Reason}", reason);
        return new BootManagerSwapResult(false, [], null, reason);
    }

    private BootManagerSwapResult Unverified(Ca2023Mode mode, string details)
    {
        if (mode == Ca2023Mode.Required)
        {
            throw new BootrixException(ErrorCode.BootManager2023Unverified, details) { Arguments = [details] };
        }

        _logger.LogWarning("The 2023 boot files did not check out and were taken back: {Details}", details);
        return new BootManagerSwapResult(false, [], null, details);
    }

    private static string Label(string relative) => relative.Replace('\\', '/');

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only scratch files; they go with the job's work folder.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Only a scratch file.
        }
    }
}
