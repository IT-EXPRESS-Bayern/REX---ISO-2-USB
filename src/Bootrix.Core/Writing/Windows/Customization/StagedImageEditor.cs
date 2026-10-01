// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bootrix.Core.Errors;
using Bootrix.Core.Tiny;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Core.Writing.Windows.Customization;

/// <summary>
/// Changes one or several images of a WIM that already sits on the finished medium. The WIM is copied to the work
/// folder, mounted from there and written back under a temporary name before it replaces the original, so
/// that a failed mount or commit cannot leave a damaged file on the stick, and so that DISM never has to deal with
/// the volume path of a removable device.
/// </summary>
public sealed class StagedImageEditor(IImageServicing servicing, IImageFileSystem files, ILogger? logger = null)
{
    // Room for DISM's own bookkeeping and for the image to grow by what the edit adds.
    private const long SlackBytes = 512L << 20;

    private const string TemporarySuffix = ".bootrix-new";

    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <param name="imagePath">The WIM on the medium; replaced when all edits are committed.</param>
    /// <param name="indexes">Images to edit, in order; each one is mounted, edited and committed on its own.</param>
    /// <param name="edit">Gets the index and the folder the image is mounted at.</param>
    public async Task EditAsync(
        string imagePath,
        IReadOnlyList<int> indexes,
        string workDirectory,
        Func<int, string, CancellationToken, Task> edit,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(imagePath);
        ArgumentException.ThrowIfNullOrEmpty(workDirectory);
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(progress);
        if (indexes.Count == 0)
        {
            throw new ArgumentException("At least one image index is required.", nameof(indexes));
        }

        var length = new FileInfo(imagePath).Length;
        Directory.CreateDirectory(workDirectory);
        var needed = length + SlackBytes;
        if (files.GetFreeBytes(workDirectory) < needed)
        {
            throw new BootrixException(ErrorCode.InsufficientSpace, workDirectory)
            {
                Arguments = [workDirectory, FormatGigabytes(needed)],
            };
        }

        var weights = new double[indexes.Count + 2];
        weights[0] = 1;
        weights[^1] = 1.5;
        Array.Fill(weights, 4, 1, indexes.Count);
        var stages = new StagedProgress(progress, weights);

        var staging = Path.Combine(workDirectory, "stage-" + Guid.NewGuid().ToString("N")[..8]);
        var mountDirectory = Path.Combine(staging, "mount");
        var staged = Path.Combine(staging, Path.GetFileName(imagePath));
        var temporary = imagePath + TemporarySuffix;
        try
        {
            Directory.CreateDirectory(mountDirectory);
            _logger.LogInformation("Staging {Image} ({Megabytes} MB) for editing", Path.GetFileName(imagePath), length >> 20);
            await FileCopy.CopyFileAsync(imagePath, staged, stages.Stage(0), cancellationToken).ConfigureAwait(false);
            stages.Complete(0);

            for (var i = 0; i < indexes.Count; i++)
            {
                await EditOneAsync(staged, indexes[i], mountDirectory, edit, stages, i + 1, cancellationToken).ConfigureAwait(false);
            }

            await ReplaceAsync(staged, imagePath, temporary, stages.Stage(weights.Length - 1), cancellationToken).ConfigureAwait(false);
            stages.Complete(weights.Length - 1);
        }
        finally
        {
            TryDelete(temporary);
            TryDeleteDirectory(staging);
        }
    }

    private async Task EditOneAsync(
        string staged,
        int index,
        string mountDirectory,
        Func<int, string, CancellationToken, Task> edit,
        StagedProgress stages,
        int stage,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(mountDirectory);

        // Disposing without a commit discards the changes, which is what a cancel or a failed edit needs.
        await using var image = await servicing.MountAsync(staged, index, mountDirectory, stages.Stage(stage, 0, 0.4), cancellationToken).ConfigureAwait(false);
        await edit(index, image.MountDirectory, cancellationToken).ConfigureAwait(false);
        stages.Stage(stage).Report(0.5);

        cancellationToken.ThrowIfCancellationRequested();
        await image.UnmountAsync(commit: true, cancellationToken).ConfigureAwait(false);
        stages.Complete(stage);
        _logger.LogInformation("Image {Index} of {Image} edited and committed", index, Path.GetFileName(staged));
    }

    private static async Task ReplaceAsync(string staged, string target, string temporary, IProgress<double> progress, CancellationToken cancellationToken)
    {
        try
        {
            await FileCopy.CopyFileAsync(staged, temporary, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException ex) when (IsDiskFull(ex))
        {
            throw new BootrixException(ErrorCode.InsufficientSpace, target, ex)
            {
                Arguments = [Path.GetPathRoot(target) ?? target, FormatGigabytes(new FileInfo(staged).Length)],
            };
        }

        // The original stays until this point, so a full or removed stick leaves a complete image behind.
        // Files that came from an ISO are read-only, which would keep Windows from replacing them.
        File.SetAttributes(target, FileAttributes.Normal);
        File.Move(temporary, target, overwrite: true);
    }

    private static string FormatGigabytes(long bytes) => (bytes / (double)(1L << 30)).ToString("0.#", CultureInfo.InvariantCulture) + " GB";

    private static bool IsDiskFull(IOException ex) => ex.HResult is unchecked((int)0x80070070) or unchecked((int)0x80070027) or 28;

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
            // A leftover scratch folder is cleaned with the job's work directory.
        }
    }
}
