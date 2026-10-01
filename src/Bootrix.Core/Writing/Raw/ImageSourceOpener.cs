// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Images;
using Bootrix.Core.Images.Apple;
using Bootrix.Core.Images.Compression;
using Microsoft.Extensions.Logging;

namespace Bootrix.Core.Writing.Raw;

/// <summary>
/// Turns an image file into the stream a raw write reads. What the file is comes from its first bytes (and, for
/// Apple containers, its last): compressed files are decoded on the fly, UDIF and sparse images are unpacked to the
/// volume they hold. The extension is never trusted over the content; it only settles the one format that has no
/// magic number (LZMA-alone, which is accepted as <c>.lzma</c> only) and gives a mislabelled file a name in the log.
/// </summary>
public static class ImageSourceOpener
{
    /// <summary>
    /// Opens <paramref name="path"/> for writing: the stream delivers the decoded image. Its length is known for
    /// everything but gzip, bzip2 and LZMA streams without a size field; the writer stops by itself when more
    /// data comes than a target can hold, so no limit is needed here.
    /// </summary>
    public static ImageSource Open(string path, ImageSourceOptions? options = null, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        options ??= new ImageSourceOptions();

        if (Directory.Exists(path))
        {
            return OpenApple(path, options, logger);
        }

        var format = DetectCompression(path);
        if (format != CompressionFormat.None)
        {
            return OpenCompressed(path, format, options);
        }

        if (IsAppleContainer(path))
        {
            return OpenApple(path, options, logger);
        }

        var file = OpenFile(path, FileShare.Read, FileOptions.SequentialScan | FileOptions.Asynchronous, 1024 * 1024);
        return WithBlockMap(new ImageSource(file, file.Length, ImageSourceKind.File), path, options);
    }

    /// <summary>
    /// Opens <paramref name="path"/> for looking at it: a seekable stream. Files and compressed files are returned as
    /// they are (the image inspector decodes by itself), Apple containers as the volume they hold, because the
    /// container says nothing about the disk inside.
    /// </summary>
    public static ImageSource OpenForInspection(string path, ImageSourceOptions? options = null, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        options ??= new ImageSourceOptions();

        if (Directory.Exists(path))
        {
            return OpenApple(path, options, logger);
        }

        var format = DetectCompression(path);
        if (format == CompressionFormat.None && IsAppleContainer(path))
        {
            return OpenApple(path, options, logger);
        }

        // The inspector reads images that are still being downloaded, so the file may be open for writing elsewhere.
        var file = OpenFile(path, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess, bufferSize: 1);
        return new ImageSource(file, file.Length, format == CompressionFormat.None ? ImageSourceKind.File : ImageSourceKind.Compressed)
        {
            Compression = format,
        };
    }

    private static ImageSource OpenCompressed(string path, CompressionFormat format, ImageSourceOptions options)
    {
        var decoded = CompressedImageStream.Open(path, new CompressedImageOptions { EntryName = options.ArchiveEntry });
        var source = new ImageSource(decoded, decoded.UncompressedLength, ImageSourceKind.Compressed)
        {
            Compression = format,
            ArchiveEntry = decoded.EntryName,
        };
        return WithBlockMap(source, path, options);
    }

    private static ImageSource OpenApple(string path, ImageSourceOptions options, ILogger? logger)
    {
        if (Directory.Exists(path) && !options.AllowSparseBundle)
        {
            throw ImageFailures.Unsupported("sparse bundle (its band files cannot be opened on behalf of another user)");
        }

        var apple = AppleImageSource.OpenForRawWrite(path, options.SectorSize, logger: logger);
        return new ImageSource(apple.Stream, apple.Length, ImageSourceKind.AppleContainer)
        {
            AppleContainer = apple.Container,
            Owner = apple,
        };
    }

    private static CompressionFormat DetectCompression(string path)
    {
        Span<byte> head = stackalloc byte[CompressionSniffer.HeaderLength];
        int read;
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            read = RandomAccess.Read(handle, head, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw ImageFailures.Unreadable($"{path}: {ex.Message}", ex);
        }

        var format = CompressionSniffer.Detect(head[..read]);

        // LZMA-alone has no signature, only a plausible header; a raw disk image can look like one, so the name has to agree.
        return format == CompressionFormat.Lzma && !Path.GetExtension(path).Equals(".lzma", StringComparison.OrdinalIgnoreCase)
            ? CompressionFormat.None
            : format;
    }

    /// <summary>UDIF and sparse images; containers that are recognised but unusable (encrypted, segmented, Mac OS 9) throw their specific error.</summary>
    private static bool IsAppleContainer(string path)
    {
        using var source = RandomAccessSource.OpenFile(path);
        var signature = ContainerSniffer.Detect(source);
        ContainerSniffer.ThrowIfUnsupported(signature);
        return signature is ContainerSignature.Udif or ContainerSignature.SparseImage;
    }

    private static FileStream OpenFile(string path, FileShare share, FileOptions options, int bufferSize)
    {
        try
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, share, bufferSize, options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw ImageFailures.Unreadable($"{path}: {ex.Message}", ex);
        }
    }

    private static ImageSource WithBlockMap(ImageSource source, string path, ImageSourceOptions options)
    {
        if (options.BlockMap == BlockMapUse.Off || BlockMapLocator.Find(path) is not { } mapPath)
        {
            return source;
        }

        try
        {
            var map = BlockMapParser.Parse(mapPath);
            string? problem = null;
            if (map.FileChecksumValid == false)
            {
                problem = "the checksum recorded in the block map does not match the file";
            }
            else if (source.Length is { } length && !map.Describes(length))
            {
                problem = $"the block map describes an image of {map.ImageSize} bytes, this one has {length}";
            }

            return problem is null
                ? Copy(source, map, null, options)
                : Copy(source, null, $"{Path.GetFileName(mapPath)}: {problem}", options);
        }
        catch (BootrixException ex) when (ex.Code == ErrorCode.BlockMapInvalid)
        {
            return Copy(source, null, $"{Path.GetFileName(mapPath)}: {ex.Message}", options);
        }
    }

    private static ImageSource Copy(ImageSource source, BlockMap? map, string? skipped, ImageSourceOptions options) =>
        new(source.Stream, source.Length, source.Kind)
        {
            Compression = source.Compression,
            ArchiveEntry = source.ArchiveEntry,
            BlockMap = map,
            BlockMapSkipped = skipped,
            FillBlockMapGaps = options.BlockMap == BlockMapUse.FillGaps,
        };
}
