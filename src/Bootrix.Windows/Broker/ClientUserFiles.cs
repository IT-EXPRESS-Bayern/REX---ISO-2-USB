// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers;
using Bootrix.Core.Errors;
using Bootrix.Windows.Tiny;

namespace Bootrix.Windows.Broker;

/// <summary>
/// Reads and writes the user's files with the rights of the client on the broker pipe. The file is opened while
/// impersonating, which is where the access check happens; the copy itself then runs with the open handle.
/// </summary>
public sealed class ClientUserFiles(IClientImpersonator impersonator) : IUserFiles
{
    private const int BufferSize = 1 << 20;
    private const string PartialSuffix = ".bootrix-part";

    public async Task CopyInAsync(string userPath, string localPath, Action<long, long> progress, CancellationToken cancellationToken)
    {
        FileStream source;
        try
        {
            source = await impersonator.RunAsClientAsync(() => Task.FromResult(OpenForReading(userPath))).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BootrixException(ErrorCode.ImageUnreadable, $"{userPath}: {ex.Message}", ex) { Arguments = [userPath] };
        }

        await using (source.ConfigureAwait(false))
        {
            await using var target = new FileStream(localPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await CopyAsync(source, target, source.Length, progress, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task CopyOutAsync(string localPath, string userPath, Action<long, long> progress, CancellationToken cancellationToken)
    {
        var partial = userPath + PartialSuffix;
        try
        {
            await using (var source = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var target = await impersonator.RunAsClientAsync(() => Task.FromResult(CreateForWriting(partial))).ConfigureAwait(false))
            {
                await CopyAsync(source, target, source.Length, progress, cancellationToken).ConfigureAwait(false);
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            await impersonator.RunAsClientAsync(() =>
            {
                File.Move(partial, userPath, overwrite: true);
                return Task.FromResult(true);
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await RemovePartialAsync(partial).ConfigureAwait(false);
            if (ex is IOException or UnauthorizedAccessException)
            {
                throw new BootrixException(ErrorCode.FileCopyFailed, $"{userPath}: {ex.Message}", ex) { Arguments = [userPath, ex.Message] };
            }

            throw;
        }
    }

    private static FileStream OpenForReading(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static FileStream CreateForWriting(string path) =>
        new(path, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async Task CopyAsync(Stream source, Stream target, long total, Action<long, long> progress, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), cancellationToken).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                done += read;
                progress(done, total);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task RemovePartialAsync(string partial)
    {
        try
        {
            await impersonator.RunAsClientAsync(() =>
            {
                File.Delete(partial);
                return Task.FromResult(true);
            }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Nothing more can be done; the leftover carries the .bootrix-part suffix and says what it is.
        }
    }
}
