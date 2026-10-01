// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Images;
using Bootrix.Core.Images.Iso;

namespace Bootrix.Core.Writing.Linux;

public sealed record IsoFile(string Path, long Length);

/// <summary>A read-only file tree: the files of an image, or of the boot image inside it.</summary>
public interface IImageFileTree
{
    IReadOnlyList<IsoFile> Files { get; }

    IReadOnlyList<string> Directories { get; }

    Stream OpenFile(string path);
}

/// <summary>
/// The file tree of an ISO image (UDF, or ISO 9660 with Rock Ridge or Joliet names) opened for copying.
/// The same reader that analysed the image is used, so the files are the ones the profile was made from.
/// </summary>
public sealed class IsoContent : IImageFileTree, IDisposable
{
    private const int EntryLimit = 300_000;

    private readonly Stream _iso;
    private readonly ImageFileSystem _tree;
    private readonly ElToritoCatalog? _catalog;
    private ImageFileSystem? _efiImage;
    private bool _efiImageLoaded;

    private IsoContent(Stream iso, ImageFileSystem tree, string? volumeLabel, ElToritoCatalog? catalog)
    {
        _iso = iso;
        _tree = tree;
        VolumeLabel = volumeLabel;
        _catalog = catalog;
        Files = [.. tree.Index.Files.Select(file => new IsoFile(file.Path, file.Length)).OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)];
        Directories = [.. tree.Index.Directories.Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The label of the volume; what boot configurations call "the CD".</summary>
    public string? VolumeLabel { get; }

    public IReadOnlyList<IsoFile> Files { get; }

    public IReadOnlyList<string> Directories { get; }

    public long TotalBytes => Files.Sum(file => file.Length);

    /// <summary>The ISO stream has to stay open and seekable for as long as this object is used.</summary>
    /// <exception cref="BootrixException">The stream is not an ISO 9660 or UDF image whose file system can be read.</exception>
    public static IsoContent Open(Stream iso, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(iso);
        if (!iso.CanSeek)
        {
            throw Unreadable("the image stream is not seekable");
        }

        var volume = Iso9660Reader.Read(iso) ?? throw Unreadable("no ISO 9660 volume descriptor found");
        var catalog = volume.BootCatalogSector is { } sector ? ElToritoParser.ReadCatalog(iso, sector) : null;
        var tree = ImageFileSystem.OpenIso(iso, volume.HasJoliet, volume.HasUdfRecognition, EntryLimit, cancellationToken)
            ?? throw Unreadable("the file system of the image cannot be read");
        if (tree.Index.Incomplete)
        {
            tree.Dispose();
            throw Unreadable("the file tree of the image is incomplete");
        }

        return new IsoContent(iso, tree, volume.VolumeId, catalog);
    }

    public bool Contains(string path) => _tree.Index.HasFile(path);

    public bool ContainsDirectory(string path) => _tree.Index.HasDirectory(path);

    public IEnumerable<string> Find(string glob) => _tree.Index.FindFiles(glob);

    public long? LengthOf(string path) => _tree.Index.LengthOf(path);

    public Stream OpenFile(string path) =>
        _tree.OpenFile(path) ?? throw Unreadable($"{path} cannot be read from the image");

    /// <summary>Reads a text file as UTF-8, with a Latin-1 fallback for stray bytes; null when the file is absent.</summary>
    public string? ReadText(string path, int maxBytes = 4 * 1024 * 1024) => _tree.ReadText(path, maxBytes);

    /// <summary>
    /// The FAT image that the El Torito catalog names for EFI booting, as a file tree of its own. Hybrid images such as
    /// Solus keep their EFI loaders only in there; null when the image has none or it cannot be read.
    /// </summary>
    public IsoContentView? EfiImage()
    {
        if (!_efiImageLoaded)
        {
            _efiImageLoaded = true;
            var entry = _catalog?.EfiEntries.FirstOrDefault();
            _efiImage = entry is null ? null : BootImageReader.Open(_iso, entry, EntryLimit, CancellationToken.None);
        }

        return _efiImage is null ? null : new IsoContentView(_efiImage);
    }

    public void Dispose()
    {
        _tree.Dispose();
        _efiImage?.Dispose();
    }

    internal static BootrixException Unreadable(string detail) =>
        new(ErrorCode.ImageUnreadable, detail) { Arguments = [detail] };
}

/// <summary>A file tree that belongs to an <see cref="IsoContent"/>, such as its EFI boot image.</summary>
public sealed class IsoContentView : IImageFileTree
{
    private readonly ImageFileSystem _tree;

    internal IsoContentView(ImageFileSystem tree) => _tree = tree;

    public IReadOnlyList<IsoFile> Files => [.. _tree.Index.Files.Select(file => new IsoFile(file.Path, file.Length)).OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)];

    public IReadOnlyList<string> Directories => [.. _tree.Index.Directories.Order(StringComparer.OrdinalIgnoreCase)];

    public Stream OpenFile(string path) =>
        _tree.OpenFile(path) ?? throw IsoContent.Unreadable($"{path} cannot be read from the boot image");
}
