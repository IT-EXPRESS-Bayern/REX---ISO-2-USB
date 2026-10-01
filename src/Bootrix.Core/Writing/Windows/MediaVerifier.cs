// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Bootrix.Core.Errors;
using Bootrix.Core.Images.Wim;

namespace Bootrix.Core.Writing.Windows;

public sealed record VerificationResult(int Files, long Bytes);

/// <summary>
/// Reads the files back from the target and compares them with what the copy recorded. Only worth anything
/// when the caller has made Windows forget its cache first (flush, dismount) so the bytes come from the device.
/// </summary>
public static partial class MediaVerifier
{
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(100);

    /// <param name="bytesRead">Receives the number of bytes read so far.</param>
    /// <exception cref="BootrixException">A file is missing, has another length or other contents; or a split image is not a complete set.</exception>
    public static async Task<VerificationResult> VerifyAsync(
        string root,
        IReadOnlyList<CopiedFile> files,
        IProgress<long>? bytesRead = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        ArgumentNullException.ThrowIfNull(files);

        var pump = new StreamPump(WindowsMediaCopier.BufferBytes);
        var since = System.Diagnostics.Stopwatch.StartNew();
        long done = 0;
        long lastReported = -1;

        void Advance(int count)
        {
            done += count;
            if (since.Elapsed >= ReportInterval)
            {
                since.Restart();
                lastReported = done;
                bytesRead?.Report(done);
            }
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = WindowsMediaCopier.DestinationPath(root, file.Path);
            if (!File.Exists(path) || new FileInfo(path).Length != file.Length)
            {
                throw Mismatch(file.Path, "missing or of another length");
            }

            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 0, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await pump.HashAsync(input, Advance, cancellationToken).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(hash, file.Sha256))
            {
                throw Mismatch(file.Path, "contents differ");
            }
        }

        foreach (var first in files.Where(IsFirstSwmPart))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CheckSplitSet(root, first.Path, files);
        }

        if (done != lastReported)
        {
            bytesRead?.Report(done);
        }

        return new VerificationResult(files.Count, done);
    }

    /// <summary>
    /// A set of .swm parts only works together: every part must be there, say that it is part n of the same
    /// total and belong to the same image, and be as long as the tables in its own header need.
    /// </summary>
    internal static void CheckSplitSet(string root, string firstPart, IReadOnlyList<CopiedFile> written)
    {
        var count = 0;
        while (written.Any(file => file.Path.Equals(WindowsCopyPlan.PartName(firstPart, count + 1), StringComparison.OrdinalIgnoreCase)))
        {
            count++;
        }

        Guid? id = null;
        var header = new byte[WimHeader.Size];
        for (var number = 1; number <= count; number++)
        {
            var relative = WindowsCopyPlan.PartName(firstPart, number);
            var path = WindowsMediaCopier.DestinationPath(root, relative);
            WimHeader parsed;
            long length;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1);
                length = stream.Length;
                stream.ReadExactly(header);
                parsed = WimHeader.Parse(header);
            }
            catch (Exception ex) when (ex is IOException or BootrixException)
            {
                throw SplitInvalid($"{relative} cannot be read as a WIM part", ex);
            }

            if (parsed.PartNumber != number || parsed.TotalParts != count)
            {
                throw SplitInvalid($"{relative} says it is part {parsed.PartNumber} of {parsed.TotalParts}, expected {number} of {count}");
            }

            id ??= parsed.Id;
            if (parsed.Id != id)
            {
                throw SplitInvalid($"{relative} belongs to another image than {firstPart}");
            }

            if (length < parsed.ExpectedLength)
            {
                throw SplitInvalid($"{relative} is {length} bytes long but its header describes {parsed.ExpectedLength}");
            }
        }
    }

    private static bool IsFirstSwmPart(CopiedFile file) => FirstPart().IsMatch(file.Path);

    private static BootrixException Mismatch(string path, string detail) =>
        new(ErrorCode.MediaFileMismatch, $"{path}: {detail}") { Arguments = [path] };

    private static BootrixException SplitInvalid(string detail, Exception? inner = null) =>
        new(ErrorCode.SplitSetInvalid, detail, inner) { Arguments = [detail] };

    /// <summary>"install.swm" but not "install2.swm": the first part has no number.</summary>
    [GeneratedRegex(@"[^/\d]\.swm$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FirstPart();
}
