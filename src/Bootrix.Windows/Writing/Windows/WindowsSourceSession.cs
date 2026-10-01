// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Images;
using Bootrix.Core.Images.Compression;
using Bootrix.Core.Jobs;
using Bootrix.Core.Writing.Windows;
using Bootrix.Windows.Images;
using Bootrix.Windows.Jobs;
using Microsoft.Extensions.Logging;

namespace Bootrix.Windows.Writing.Windows;

/// <summary>
/// Opens the files of a Windows ISO for copying. The fast way is to let Windows attach the ISO and read it like a
/// disc; if that does not work, or shows something other than what the inspection saw, the files are read straight
/// from the image stream.
/// </summary>
internal static class WindowsSourceSession
{
    private static readonly ImageContainer[] SupportedContainers = [ImageContainer.Iso9660, ImageContainer.IsoUdfBridge, ImageContainer.Udf];

    public static async Task<IWindowsMediaSource> OpenAsync(
        IImageStreamProvider images,
        string imagePath,
        ImageInspection inspection,
        JobContext context,
        ILogger log,
        CancellationToken cancellationToken)
    {
        if (!SupportedContainers.Contains(inspection.Container))
        {
            throw new BootrixException(ErrorCode.ImageUnsupported, $"{inspection.Container} is not an optical disc image") { Arguments = [inspection.Container.ToString()] };
        }

        // The stream proves that the user may read the file (the broker opens it with the user's rights), and as long
        // as it stays open nobody can swap the file for another one between this check and the mount below.
        var opened = await images.OpenAsync(imagePath, cancellationToken).ConfigureAwait(false);
        context.OnCleanup(opened.DisposeAsync);

        if (inspection.Compression == CompressionFormat.None && inspection.ArchiveEntry is null)
        {
            var mounted = await TryMountAsync(imagePath, inspection, context, log, cancellationToken).ConfigureAwait(false);
            if (mounted is not null)
            {
                return mounted;
            }
        }

        log.LogInformation("Reading the files straight from the image");
        var source = await Task.Run(() => IsoMediaSource.Open(opened.Stream, inspection, cancellationToken), cancellationToken).ConfigureAwait(false);
        context.OnCleanup(source);
        return source;
    }

    private static async Task<IWindowsMediaSource?> TryMountAsync(
        string imagePath, ImageInspection inspection, JobContext context, ILogger log, CancellationToken cancellationToken)
    {
        MountedImage? mounted = null;
        DirectoryMediaSource? source = null;
        try
        {
            mounted = await Task.Run(() => VirtualDiskMounter.MountIso(imagePath, cancellationToken), cancellationToken).ConfigureAwait(false);
            var root = mounted.RootPath ?? throw new BootrixException(ErrorCode.ImageMountFailed, "the attached image has no drive letter");
            source = await Task.Run(() => DirectoryMediaSource.Scan(root, cancellationToken), cancellationToken).ConfigureAwait(false);

            if (!ShowsTheSameFiles(source, inspection))
            {
                log.LogWarning(
                    "The attached image shows {Files} files and {Bytes} bytes, the inspection found {ExpectedFiles} and {ExpectedBytes}; reading from the image stream instead",
                    source.Files.Count,
                    source.Files.Sum(file => file.Length),
                    inspection.FileCount,
                    inspection.Profile.TotalBytes);
                source.Dispose();
                mounted.Dispose();
                return null;
            }

            context.OnCleanup(mounted);
            context.OnCleanup(source);
            log.LogInformation("Reading the files from the attached image {Root}", root);
            return source;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "The image could not be attached; reading the files from the image stream instead");
            source?.Dispose();
            mounted?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Windows shows an ISO through UDF or ISO 9660, whichever it prefers. A tree that has lost files, or names, to the
    /// shorter ISO 9660 rules would produce a medium that does not start, so the two views must agree.
    /// </summary>
    internal static bool ShowsTheSameFiles(IWindowsMediaSource source, ImageInspection inspection)
    {
        var bytes = source.Files.Sum(file => file.Length);
        var expectedBytes = inspection.Profile.TotalBytes;
        return source.Files.Count == inspection.FileCount && (expectedBytes == 0 || bytes == expectedBytes);
    }
}
