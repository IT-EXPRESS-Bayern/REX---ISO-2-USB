// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Model;

namespace Bootrix.Core.Writing.Windows.Customization;

public enum AnswerFileOutcome
{
    Written,

    /// <summary>The image's own file stays; nothing was written.</summary>
    LeftAlone,
}

/// <param name="Path">The answer file on the medium; null when nothing was written.</param>
/// <param name="OriginalBackup">Where the image's own file went, when there was one and it was kept.</param>
public sealed record AnswerFileResult(AnswerFileOutcome Outcome, string? Path, string? OriginalBackup);

/// <summary>Puts autounattend.xml at the root of the medium without losing or half-writing the file that may already be there.</summary>
public static class AnswerFileWriter
{
    public const string FileName = "autounattend.xml";
    public const string BackupName = "autounattend.xml.original";

    private const string TemporaryName = "autounattend.xml.bootrix-new";

    public static AnswerFileResult Write(string mediaRoot, byte[] content, ExistingAnswerFilePolicy policy)
    {
        ArgumentException.ThrowIfNullOrEmpty(mediaRoot);
        ArgumentNullException.ThrowIfNull(content);

        var existing = FindExisting(mediaRoot);
        if (existing is not null)
        {
            switch (policy)
            {
                case ExistingAnswerFilePolicy.Keep:
                    return new AnswerFileResult(AnswerFileOutcome.LeftAlone, null, null);
                case ExistingAnswerFilePolicy.Fail:
                    throw new BootrixException(ErrorCode.AnswerFileExists, existing);
            }
        }

        // Written under another name first: a full stick must not leave a truncated answer file that Setup would read.
        var temporary = Path.Combine(mediaRoot, TemporaryName);
        var target = Path.Combine(mediaRoot, FileName);
        try
        {
            File.WriteAllBytes(temporary, content);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }

        string? backup = null;
        try
        {
            if (existing is not null)
            {
                // Files copied from an ISO are read-only.
                File.SetAttributes(existing, FileAttributes.Normal);
                if (policy == ExistingAnswerFilePolicy.ReplaceAndKeepOriginal)
                {
                    backup = Path.Combine(mediaRoot, BackupName);
                    File.Move(existing, backup, overwrite: true);
                }
                else
                {
                    File.Delete(existing);
                }
            }

            File.Move(temporary, target, overwrite: true);
        }
        catch
        {
            TryDelete(temporary);
            if (backup is not null && existing is not null && !File.Exists(existing))
            {
                File.Move(backup, existing);
            }

            throw;
        }

        return new AnswerFileResult(AnswerFileOutcome.Written, target, backup);
    }

    /// <summary>The names on an ISO are upper case more often than not, and the media may be on a case-sensitive test folder.</summary>
    private static string? FindExisting(string mediaRoot) =>
        Directory.EnumerateFiles(mediaRoot)
            .FirstOrDefault(path => string.Equals(Path.GetFileName(path), FileName, StringComparison.OrdinalIgnoreCase));

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The leftover is harmless, and the original error is the one to report.
        }
    }
}
