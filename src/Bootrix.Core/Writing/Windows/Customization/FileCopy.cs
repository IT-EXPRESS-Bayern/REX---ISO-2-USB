// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Writing.Windows.Customization;

internal static class FileCopy
{
    public const int BufferBytes = 1024 * 1024;

    /// <summary>Copies a stream in large blocks, reporting the bytes written so far, and checks for cancellation between blocks.</summary>
    public static async Task CopyAsync(Stream source, Stream destination, Action<long>? written, CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferBytes];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            total += read;
            written?.Invoke(total);
        }
    }

    /// <summary>Copies a file to a new name; the destination is replaced and made writable.</summary>
    public static async Task CopyFileAsync(string source, string destination, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        await using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, BufferBytes, FileOptions.SequentialScan | FileOptions.Asynchronous))
        await using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, BufferBytes, FileOptions.Asynchronous))
        {
            var length = Math.Max(1, input.Length);
            await CopyAsync(input, output, done => progress?.Report((double)done / length), cancellationToken).ConfigureAwait(false);
        }

        File.SetAttributes(destination, FileAttributes.Normal);
        progress?.Report(1);
    }
}
