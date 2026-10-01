// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Windows.Jobs;

/// <summary>An image opened for sequential reading. <see cref="Length"/> is null when the size cannot be known in advance (a stream compressed without a size field).</summary>
public sealed class OpenedImage(Stream stream, long? length) : IAsyncDisposable
{
    public Stream Stream { get; } = stream;

    public long? Length { get; } = length;

    public ValueTask DisposeAsync() => Stream.DisposeAsync();
}

/// <summary>Turns an image path into a readable stream; compressed and container formats plug in here.</summary>
public interface IImageStreamProvider
{
    Task<OpenedImage> OpenAsync(string path, CancellationToken cancellationToken);
}

public sealed class FileImageStreamProvider : IImageStreamProvider
{
    public Task<OpenedImage> OpenAsync(string path, CancellationToken cancellationToken)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
        return Task.FromResult(new OpenedImage(stream, stream.Length));
    }
}
