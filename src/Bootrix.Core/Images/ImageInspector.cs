// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Images.Compression;
using Bootrix.Core.Images.Integrity;
using Microsoft.Extensions.Logging;

namespace Bootrix.Core.Images;

public sealed record ImageInspectOptions
{
    /// <summary>Entry to inspect when the image is a zip archive; by default the archive's image is picked.</summary>
    public string? ArchiveEntry { get; init; }

    /// <summary>
    /// How much of a compressed image is decoded for the analysis. Whatever fits is analysed completely; beyond that
    /// the partition tables, volume descriptors and FAT volumes near the start are still examined, but not ISO file trees.
    /// </summary>
    public int MaxDecodedPrefixBytes { get; init; } = 8 * 1024 * 1024;
}

/// <summary>
/// Looks into an image file and reports what it is, how it boots and whether it is complete, without writing
/// anything. Entry point of the image analysis; its <see cref="ImageInspection.Profile"/> is what the layout planner consumes.
/// </summary>
public sealed class ImageInspector(ILogger<ImageInspector>? logger = null)
{
    private static readonly string[] BmapSuffixes = [".bmap"];

    public async Task<ImageInspection> InspectAsync(string path, ImageInspectOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);

        // Shared with writers so that an image that is still being downloaded can be looked at.
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.RandomAccess);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw ImageFailures.Unreadable($"{path}: {ex.Message}", ex);
        }

        await using (stream.ConfigureAwait(false))
        {
            var inspection = await InspectAsync(stream, path, options, cancellationToken).ConfigureAwait(false);
            return inspection with { BmapPath = FindBmap(path) };
        }
    }

    /// <summary>Inspects an image that is available as a seekable stream; <paramref name="fileName"/> is only used as a hint.</summary>
    public Task<ImageInspection> InspectAsync(Stream stream, string? fileName = null, ImageInspectOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            throw new ArgumentException("The image stream must be seekable.", nameof(stream));
        }

        return Task.Run(() => Inspect(stream, fileName, options ?? new ImageInspectOptions(), cancellationToken), cancellationToken);
    }

    private ImageInspection Inspect(Stream stream, string? fileName, ImageInspectOptions options, CancellationToken cancellationToken)
    {
        var length = stream.Length;
        var head = new byte[CompressionSniffer.HeaderLength];
        stream.Position = 0;
        var format = CompressionSniffer.Detect(head.AsSpan(0, stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false)));
        stream.Position = 0;

        var nameFindings = fileName is null ? [] : ImageIntegrityChecker.CheckName(fileName);
        var inspection = format == CompressionFormat.None
            ? ImageAnalyzer.Analyze(stream, fileName, length, partial: false, cancellationToken)
            : InspectCompressed(stream, fileName, format, options, cancellationToken);

        logger?.LogDebug(
            "Inspected {File}: {Container}, {Kind}, family {Family}, {Warnings} findings",
            fileName, inspection.Container, inspection.Profile.Kind, inspection.Profile.Family ?? "-", inspection.Warnings.Count);
        return inspection with { Warnings = [.. nameFindings, .. inspection.Warnings], IsTruncated = inspection.IsTruncated || nameFindings.Count > 0 };
    }

    private static ImageInspection InspectCompressed(Stream stream, string? fileName, CompressionFormat format, ImageInspectOptions options, CancellationToken cancellationToken)
    {
        CompressedImageStream decoded;
        try
        {
            decoded = CompressedImageStream.Open(
                stream,
                new CompressedImageOptions { EntryName = options.ArchiveEntry, CheckStructure = false },
                leaveOpen: true);
        }
        catch (BootrixException ex) when (ex.Code == ErrorCode.ImageUnreadable)
        {
            // A zip archive without a usable central directory: nothing to look into.
            return Unreadable(stream, fileName, format, ex);
        }

        using var _ = decoded;
        var findings = ImageIntegrityChecker.CompressedFindings(format, decoded.Structure);
        using var prefix = PrefixBuffer.Read(decoded, options.MaxDecodedPrefixBytes, out var failure);
        if (failure is not null)
        {
            findings.Add(new ImageWarning(ImageWarningKeys.CompressedIncomplete, WarningSeverity.Error, format.ToString(), failure.Detail));
        }

        // An image that fits into the prefix has been decoded completely, so its length is known even for gzip.
        var wholeImage = failure is null && prefix.Length < options.MaxDecodedPrefixBytes;
        var imageLength = decoded.UncompressedLength ?? (wholeImage ? prefix.Length : null);
        var innerName = decoded.EntryName ?? (fileName is null ? null : StripCompressionExtension(fileName));

        var analysis = ImageAnalyzer.Analyze(prefix, innerName, imageLength, partial: !wholeImage, cancellationToken);
        if (!wholeImage)
        {
            findings.Add(new ImageWarning(ImageWarningKeys.AnalysisPartial, WarningSeverity.Info, prefix.Length / (1024 * 1024)));
        }

        if (imageLength is null)
        {
            findings.Add(new ImageWarning(ImageWarningKeys.CompressedSizeUnknown, WarningSeverity.Info));
        }

        return analysis with
        {
            FileName = fileName,
            FileLength = stream.Length,
            Compression = format,
            ArchiveEntry = decoded.EntryName,
            ArchiveEntries = decoded.Entries,
            ImageLength = imageLength,
            ImageLengthHint = decoded.UncompressedLengthHint,
            Profile = analysis.Profile with { IsCompressed = true, ImageBytes = imageLength ?? 0 },
            Warnings = [.. findings, .. analysis.Warnings],
            IsTruncated = analysis.IsTruncated || findings.Any(finding => finding.Severity == WarningSeverity.Error),
        };
    }

    private static ImageInspection Unreadable(Stream stream, string? fileName, CompressionFormat format, BootrixException failure) => new()
    {
        Profile = new ImageProfile { Kind = ImageKind.Data, IsCompressed = true },
        Container = ImageContainer.Unknown,
        FileName = fileName,
        FileLength = stream.Length,
        Compression = format,
        Warnings = [new ImageWarning(ImageWarningKeys.CompressedIncomplete, WarningSeverity.Error, format.ToString(), failure.Detail)],
        IsTruncated = true,
    };

    private static string StripCompressionExtension(string fileName) =>
        ImageFileTypes.IsCompressed(fileName) ? Path.GetFileNameWithoutExtension(fileName) : fileName;

    /// <summary>bmaptool writes <c>image.img.bmap</c> next to <c>image.img.gz</c>; the name without the compression suffix is checked too.</summary>
    private static string? FindBmap(string path)
    {
        var bare = ImageFileTypes.IsCompressed(path) ? Path.ChangeExtension(path, null) : path;
        return BmapSuffixes
            .SelectMany(suffix => new[] { path + suffix, bare + suffix, Path.ChangeExtension(bare, suffix) })
            .Distinct(StringComparer.Ordinal)
            .FirstOrDefault(File.Exists);
    }
}
