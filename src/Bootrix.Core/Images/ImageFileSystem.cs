// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Bootrix.Core.Errors;
using DiscUtils;
using DiscUtils.Fat;
using DiscUtils.Iso9660;
using DiscUtils.Streams;
using DiscUtils.Udf;
using Bootrix.Core.Images.Disk;

namespace Bootrix.Core.Images;

internal enum ImageFileSystemKind
{
    Iso9660,
    Iso9660RockRidge,
    Iso9660Joliet,
    Udf,
    Fat,
}

/// <summary>
/// A file system inside an image (ISO 9660 with Rock Ridge or Joliet, UDF, or a FAT partition) together with
/// the index of everything it contains.
/// </summary>
internal sealed class ImageFileSystem : IDisposable
{
    private const int MaxTextBytes = 16 * 1024;
    private const int MaxDirectories = 50_000;
    private const int MaxDepth = 40;

    private readonly DiscFileSystem _fileSystem;

    private ImageFileSystem(DiscFileSystem fileSystem, ImageFileSystemKind kind, ImageFileIndex index)
    {
        _fileSystem = fileSystem;
        Kind = kind;
        Index = index;
    }

    public ImageFileSystemKind Kind { get; }

    public ImageFileIndex Index { get; }

    public string? VolumeLabel => _fileSystem.VolumeLabel;

    /// <summary>
    /// Opens the best file system an ISO offers. UDF comes first because UDF bridge discs (all Windows ISOs) hold
    /// exact file sizes and long names there, while their ISO 9660 side is limited to 8.3 names and 4 GiB. For plain
    /// ISO 9660, Rock Ridge gives the real names and Joliet is the fallback.
    /// </summary>
    public static ImageFileSystem? OpenIso(Stream iso, bool joliet, bool udfPresent, int entryLimit, CancellationToken cancellationToken)
    {
        if (udfPresent && TryOpen(() => new UdfReader(iso), ImageFileSystemKind.Udf, entryLimit, cancellationToken) is { } udf)
        {
            return udf;
        }

        if (!CDReader.Detect(iso))
        {
            return null;
        }

        var rockRidge = TryOpen(() => new CDReader(iso, false, true), ImageFileSystemKind.Iso9660, entryLimit, cancellationToken, reader => ((CDReader)reader).ActiveVariant);
        if (rockRidge is { Kind: ImageFileSystemKind.Iso9660RockRidge } || !joliet)
        {
            return rockRidge;
        }

        return TryOpen(() => new CDReader(iso, true, true), ImageFileSystemKind.Iso9660Joliet, entryLimit, cancellationToken) ?? rockRidge;
    }

    public static ImageFileSystem? OpenFat(Stream partition, int entryLimit, CancellationToken cancellationToken) =>
        TryOpen(() => new FatFileSystem(partition, Ownership.None), ImageFileSystemKind.Fat, entryLimit, cancellationToken);

    /// <summary>
    /// Opens the FAT volume at <paramref name="offset"/> of <paramref name="parent"/>, or returns null when the first
    /// sector is not a FAT boot sector. The check keeps the reader away from partitions of other file systems.
    /// </summary>
    public static ImageFileSystem? OpenFatRegion(Stream parent, long offset, long length, int entryLimit, CancellationToken cancellationToken)
    {
        if (length <= 0 || offset < 0 || offset + 512 > parent.Length)
        {
            return null;
        }

        if (!FatBootSector.LooksLikeFatBootSector(ImageContainerSniffer.ReadAt(parent, offset, 512)))
        {
            return null;
        }

        return OpenFat(new SubStream(parent, Ownership.None, offset, Math.Min(length, parent.Length - offset)), entryLimit, cancellationToken);
    }

    private static ImageFileSystem? TryOpen(
        Func<DiscFileSystem> create,
        ImageFileSystemKind kind,
        int entryLimit,
        CancellationToken cancellationToken,
        Func<DiscFileSystem, Iso9660Variant>? variant = null)
    {
        DiscFileSystem? fileSystem = null;
        try
        {
            fileSystem = create();
            var effectiveKind = variant?.Invoke(fileSystem) switch
            {
                Iso9660Variant.RockRidge => ImageFileSystemKind.Iso9660RockRidge,
                Iso9660Variant.Joliet => ImageFileSystemKind.Iso9660Joliet,
                _ => kind,
            };

            return new ImageFileSystem(fileSystem, effectiveKind, BuildIndex(fileSystem, entryLimit, cancellationToken));
        }
        catch (Exception ex) when (IsParserFailure(ex))
        {
            fileSystem?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Third-party readers signal damaged images with whatever the failing line happens to throw (casts, unimplemented
    /// allocation types, bad array sizes), so everything but cancellation and our own errors counts as a damaged image.
    /// </summary>
    internal static bool IsParserFailure(Exception ex) =>
        ex is not (OperationCanceledException or BootrixException or AccessViolationException or StackOverflowException);

    private static ImageFileIndex BuildIndex(DiscFileSystem fileSystem, int limit, CancellationToken cancellationToken)
    {
        var index = new ImageFileIndex();
        var pending = new Stack<(DiscDirectoryInfo Directory, int Depth)>();
        pending.Push((fileSystem.Root, 0));
        var visited = 0;

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (directory, depth) = pending.Pop();

            // A damaged FAT or ISO can make a directory contain one of its own ancestors; depth and visit caps end that loop.
            if (++visited > MaxDirectories)
            {
                index.Incomplete = true;
                return index;
            }

            try
            {
                foreach (var child in directory.GetDirectories())
                {
                    index.AddDirectory(child.FullName);
                    if (depth + 1 < MaxDepth)
                    {
                        pending.Push((child, depth + 1));
                    }
                    else
                    {
                        index.Incomplete = true;
                    }
                }

                foreach (var file in directory.GetFiles())
                {
                    if (index.FileCount >= limit)
                    {
                        index.Incomplete = true;
                        return index;
                    }

                    index.AddFile(file.FullName, file.Length);
                }
            }
            catch (Exception ex) when (IsParserFailure(ex))
            {
                index.Incomplete = true;
            }
        }

        return index;
    }

    public Stream? OpenFile(string path)
    {
        var original = Index.OriginalPathOf(path);
        if (original is null)
        {
            return null;
        }

        try
        {
            return _fileSystem.OpenFile(original, FileMode.Open, FileAccess.Read);
        }
        catch (Exception ex) when (IsParserFailure(ex))
        {
            return null;
        }
    }

    /// <summary>The first bytes of a text file (UTF-8, falling back to Latin-1 for stray bytes), or null if it is missing.</summary>
    public string? ReadText(string path, int maxBytes = MaxTextBytes)
    {
        using var stream = OpenFile(path);
        if (stream is null)
        {
            return null;
        }

        var buffer = new byte[Math.Min(maxBytes, (int)Math.Min(stream.Length, int.MaxValue))];
        try
        {
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception ex) when (IsParserFailure(ex))
        {
            return null;
        }
    }

    public void Dispose() => _fileSystem.Dispose();
}
