// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using Bootrix.Core.Errors;
using Bootrix.Core.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Core.Writing.Windows.Customization;

/// <param name="Directory">The $WinPEDriver$ folder on the medium.</param>
/// <param name="InfFiles">Every INF that was copied, as a path on the medium.</param>
public sealed record StagedDriverSet(string Directory, IReadOnlyList<string> InfFiles, int FileCount, long Bytes);

/// <summary>
/// Copies driver folders of the user into $WinPEDriver$ at the root of the medium. Windows Setup looks for that
/// folder on every volume when it starts, loads what it finds into WinPE and schedules the drivers for
/// installation into the new system as well (Microsoft KB 2686316), so no image has to be changed for this.
/// </summary>
public sealed class DriverStager(IUserContext user, DriverLimits? limits = null, ILogger? logger = null)
{
    public const string FolderName = "$WinPEDriver$";

    private const int MaxFolderNameLength = 24;

    private readonly DriverLimits _limits = limits ?? new DriverLimits();
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <summary>
    /// Everything the user's rights allow to be read is checked first; nothing is written to the medium until all
    /// folders are accepted. A failure or cancel removes what was copied.
    /// </summary>
    public async Task<StagedDriverSet> StageAsync(IReadOnlyList<string> sourceFolders, string mediaRoot, IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourceFolders);
        ArgumentException.ThrowIfNullOrEmpty(mediaRoot);
        ArgumentNullException.ThrowIfNull(progress);

        var listings = await ScanAsync(sourceFolders, cancellationToken).ConfigureAwait(false);
        if (listings.Count == 0)
        {
            progress.Report(1);
            return new StagedDriverSet(Path.Combine(mediaRoot, FolderName), [], 0, 0);
        }

        progress.Report(0.02);

        var total = Math.Max(1, listings.Sum(listing => listing.Bytes));
        var driverRoot = Path.Combine(mediaRoot, FolderName);
        var created = new List<string>();
        var infFiles = new List<string>();
        long done = 0;
        var fileCount = 0;
        try
        {
            Directory.CreateDirectory(driverRoot);
            for (var i = 0; i < listings.Count; i++)
            {
                var listing = listings[i];
                var target = UniqueFolder(driverRoot, i + 1, Path.GetFileName(listing.Root));
                created.Add(target);

                foreach (var file in listing.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var destination = Path.Combine(target, file.RelativePath);
                    EnsureInside(target, destination);
                    var baseline = done;
                    await CopyFileAsync(
                        Path.Combine(listing.Root, file.RelativePath),
                        destination,
                        file,
                        listing.Root,
                        copied => progress.Report(0.02 + 0.98 * (baseline + copied) / total),
                        cancellationToken).ConfigureAwait(false);
                    done += file.Length;
                    fileCount++;
                    if (file.IsInf)
                    {
                        infFiles.Add(destination);
                    }
                }
            }
        }
        catch
        {
            foreach (var folder in created)
            {
                TryDeleteDirectory(folder);
            }

            TryDeleteIfEmpty(driverRoot);
            throw;
        }

        foreach (var listing in listings.Where(l => l.Skipped.Count > 0))
        {
            _logger.LogWarning(
                "Left out files of {Folder} that are not driver files: {Skipped}",
                listing.Root,
                string.Join(", ", listing.Skipped.Select(s => $"{s.Key} x{s.Value}")));
        }

        _logger.LogInformation("Copied {Count} driver files ({Megabytes} MB, {Infs} INF) from {Folders} folder(s) to {Folder}", fileCount, done >> 20, infFiles.Count, listings.Count, FolderName);
        progress.Report(1);
        return new StagedDriverSet(driverRoot, infFiles, fileCount, done);
    }

    private async Task<List<DriverFolderListing>> ScanAsync(IReadOnlyList<string> sourceFolders, CancellationToken cancellationToken)
    {
        var folders = Distinct(sourceFolders);
        var listings = new List<DriverFolderListing>(folders.Count);
        var remaining = _limits;
        foreach (var folder in folders)
        {
            var budget = remaining;
            var listing = await user.Run(() => DriverFolderScanner.Scan(folder, budget, cancellationToken)).ConfigureAwait(false);
            listings.Add(listing);

            // The limits hold for all folders together, not for each one.
            remaining = remaining with
            {
                MaxFiles = remaining.MaxFiles - listing.Files.Count,
                MaxBytes = remaining.MaxBytes - listing.Bytes,
            };
        }

        return listings;
    }

    /// <summary>Folders named twice, or inside another named folder, are copied once.</summary>
    private static List<string> Distinct(IReadOnlyList<string> folders)
    {
        var full = folders
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .Select(folder => Path.IsPathFullyQualified(folder) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) : folder)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return [.. full.Where(folder => !full.Any(other => IsInside(folder, other)))];
    }

    private static bool IsInside(string folder, string parent) =>
        folder.Length > parent.Length
        && folder.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
        && folder[parent.Length] == Path.DirectorySeparatorChar;

    private async Task CopyFileAsync(string source, string destination, DriverFile file, string root, Action<long> copied, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        // The file is opened as the user; the access check happens here, and reading goes on with the open handle.
        // Links were ruled out by the scan, and a file swapped for one since then is looked at once more.
        await using var input = await user.Run(() =>
        {
            if (DriverFolderScanner.IsLink(new FileInfo(source)))
            {
                throw LinkAppeared(root, file.RelativePath);
            }

            return new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 0, FileOptions.SequentialScan | FileOptions.Asynchronous);
        }).ConfigureAwait(false);

        long written;
        await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, FileCopy.BufferBytes, FileOptions.Asynchronous))
        {
            await FileCopy.CopyAsync(input, output, copied, cancellationToken).ConfigureAwait(false);
            written = output.Length;
        }

        File.SetAttributes(destination, FileAttributes.Normal);

        // A file that changed between the scan and the copy was not the one the limits were checked for.
        if (written != file.Length)
        {
            throw new BootrixException(ErrorCode.DriverFolderRejected, $"{source} changed while it was copied")
            {
                Arguments = [root, Localizer.Default.Get("Driver.Reject.NotReadable", file.RelativePath)],
            };
        }
    }

    /// <summary>The names were checked by the scan; this is the last line against a path that still leaves the folder.</summary>
    private static void EnsureInside(string folder, string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new BootrixException(ErrorCode.DriverFolderRejected, $"{path} leaves {folder}")
            {
                Arguments = [folder, Localizer.Default.Get("Driver.Reject.BadName", Path.GetFileName(path))],
            };
        }
    }

    private static BootrixException LinkAppeared(string root, string relative) => new(ErrorCode.DriverFolderRejected, $"{relative} became a link")
    {
        Arguments = [root, Localizer.Default.Get("Driver.Reject.Link", relative)],
    };

    /// <summary>"01-name": numbered so that equal folder names do not collide, readable so that a technician finds the folder again.</summary>
    private static string UniqueFolder(string driverRoot, int number, string? leaf)
    {
        var name = Sanitize(leaf);
        var candidate = Path.Combine(driverRoot, $"{number.ToString("D2", CultureInfo.InvariantCulture)}-{name}");
        for (var suffix = 2; Directory.Exists(candidate); suffix++)
        {
            candidate = Path.Combine(driverRoot, $"{number.ToString("D2", CultureInfo.InvariantCulture)}-{name}-{suffix.ToString(CultureInfo.InvariantCulture)}");
        }

        return candidate;
    }

    private static string Sanitize(string? leaf)
    {
        var builder = new StringBuilder();
        foreach (var c in leaf ?? "")
        {
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_');
            if (builder.Length == MaxFolderNameLength)
            {
                break;
            }
        }

        return builder.Length == 0 ? "drivers" : builder.ToString();
    }

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
            // The medium is being given up on anyway; the original error is the one to report.
        }
    }

    /// <summary>The folder may have come with the image; it is only removed when the copy created it.</summary>
    private static void TryDeleteIfEmpty(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // See above.
        }
    }
}
