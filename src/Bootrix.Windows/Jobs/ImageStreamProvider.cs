// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Writing.Raw;

namespace Bootrix.Windows.Jobs;

/// <summary>An image opened for sequential reading. <see cref="Length"/> is null when the size cannot be known in advance (a stream compressed without a size field).</summary>
public sealed class OpenedImage(Stream stream, long? length) : IAsyncDisposable
{
    public Stream Stream { get; } = stream;

    public long? Length { get; } = length;

    /// <summary>What the stream is (file, compressed, Apple container) and the block map found next to the image; null for providers that only hand out a stream.</summary>
    public ImageSource? Source { get; init; }

    public ValueTask DisposeAsync() => Source is { } source ? source.DisposeAsync() : Stream.DisposeAsync();
}

/// <summary>What the caller wants out of an image besides its bytes.</summary>
public sealed record ImageOpenOptions
{
    /// <summary>The file inside a zip archive that holds the image; the archive's image by default.</summary>
    public string? ArchiveEntry { get; init; }

    public BlockMapUse BlockMap { get; init; } = BlockMapUse.Auto;
}

/// <summary>
/// Turns an image path into a readable stream; compressed and container formats plug in here. Everything that reads
/// the user's files goes through a provider, because in the elevated broker a provider is where the access check happens.
/// </summary>
public interface IImageStreamProvider
{
    /// <summary>The decoded image for writing: decompressed, or unpacked from its Apple container.</summary>
    Task<OpenedImage> OpenAsync(string path, CancellationToken cancellationToken);

    Task<OpenedImage> OpenAsync(string path, ImageOpenOptions options, CancellationToken cancellationToken) => OpenAsync(path, cancellationToken);

    /// <summary>
    /// A seekable stream for the image inspector: the file as it is (the inspector decodes compressed files by itself),
    /// or the volume inside an Apple container.
    /// </summary>
    Task<OpenedImage> OpenForInspectionAsync(string path, ImageOpenOptions options, CancellationToken cancellationToken) => OpenAsync(path, cancellationToken);
}

/// <summary>
/// Reads images from files. A sparse bundle is a folder whose band files are opened one by one while the image is read, so
/// it is only accepted where whoever reads has the rights of whoever asked: not in the elevated broker, which therefore
/// leaves <paramref name="allowSparseBundles"/> off.
/// </summary>
public sealed class FileImageStreamProvider(bool allowSparseBundles = false) : IImageStreamProvider
{
    // The files are opened before the first await: in the broker the open runs as the user who asked, and only that open is checked.
    public Task<OpenedImage> OpenAsync(string path, CancellationToken cancellationToken) => OpenAsync(path, new ImageOpenOptions(), cancellationToken);

    public Task<OpenedImage> OpenAsync(string path, ImageOpenOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = ImageSourceOpener.Open(path, ToSourceOptions(options));
        return Task.FromResult(new OpenedImage(source.Stream, source.Length) { Source = source });
    }

    public Task<OpenedImage> OpenForInspectionAsync(string path, ImageOpenOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = ImageSourceOpener.OpenForInspection(path, ToSourceOptions(options));
        return Task.FromResult(new OpenedImage(source.Stream, source.Length) { Source = source });
    }

    private ImageSourceOptions ToSourceOptions(ImageOpenOptions options) => new()
    {
        ArchiveEntry = options.ArchiveEntry,
        BlockMap = options.BlockMap,
        AllowSparseBundle = allowSparseBundles,
    };
}
