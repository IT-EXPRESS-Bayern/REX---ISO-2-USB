// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Images;

namespace Bootrix.Core.Writing.Windows;

/// <summary>
/// Reads the files of an ISO or UDF image directly from its stream. Slower than reading an attached ISO
/// because nothing but this process caches the data, but it needs no mount and works for images that
/// Windows cannot attach, such as an ISO inside an archive.
/// </summary>
public sealed class IsoMediaSource : IWindowsMediaSource
{
    private const int EntryLimit = 300_000;

    private readonly ImageFileSystem _fileSystem;

    private IsoMediaSource(ImageFileSystem fileSystem)
    {
        _fileSystem = fileSystem;
        Files = [.. fileSystem.Index.Files.Where(file => !MediaExclusions.IsExcluded(file.Path)).Select(file => new MediaSourceFile(file.Path, file.Length))];
        Directories = [.. fileSystem.Index.Directories.Where(path => !MediaExclusions.IsExcluded(path)).Order(StringComparer.OrdinalIgnoreCase)];
    }

    public IReadOnlyList<MediaSourceFile> Files { get; }

    public IReadOnlyList<string> Directories { get; }

    /// <exception cref="BootrixException">The stream holds no readable ISO 9660 or UDF file system.</exception>
    public static IsoMediaSource Open(Stream image, ImageInspection inspection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(inspection);

        var fileSystem = ImageFileSystem.OpenIso(
            image,
            inspection.Volume?.HasJoliet ?? false,
            inspection.Container != ImageContainer.Iso9660,
            EntryLimit,
            cancellationToken);

        if (fileSystem is null)
        {
            throw new BootrixException(ErrorCode.ImageUnreadable, "no ISO 9660 or UDF file system") { Arguments = ["no readable file system"] };
        }

        if (fileSystem.Index.Incomplete)
        {
            fileSystem.Dispose();
            throw new BootrixException(ErrorCode.ImageUnreadable, "the file tree of the image is damaged") { Arguments = ["damaged file tree"] };
        }

        return new IsoMediaSource(fileSystem);
    }

    public Stream OpenRead(string path) =>
        _fileSystem.OpenFile(path) ?? throw new FileNotFoundException("The file is not part of the image.", path);

    public string? LocalPath(string path) => null;

    public void Dispose() => _fileSystem.Dispose();
}
