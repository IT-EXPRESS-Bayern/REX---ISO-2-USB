// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers;
using System.Security.Cryptography;

namespace Bootrix.Core.Library;

internal sealed record StoredImage(string Sha256, long Size, string Path, bool AlreadyPresent);

/// <summary>
/// Puts a file into a library folder under its content address. The image only ever appears under its final name
/// after it is complete: data goes to a temporary file in the same folder first and is then renamed, so another
/// workstation reading the same share never sees a partial image under a name that claims a hash.
/// </summary>
internal static class LibraryImporter
{
    private const int BufferSize = 1024 * 1024;

    public static async Task<StoredImage> StoreAsync(
        string directory,
        string source,
        string? originalFileName,
        string? knownSha256,
        bool move,
        CancellationToken cancellationToken)
    {
        var sourceFile = new FileInfo(source);
        if (!sourceFile.Exists)
        {
            throw new FileNotFoundException("The image to add does not exist.", source);
        }

        Directory.CreateDirectory(directory);

        return knownSha256 is null
            ? await StoreUnknownAsync(directory, sourceFile, originalFileName, move, cancellationToken).ConfigureAwait(false)
            : StoreKnown(directory, sourceFile, originalFileName, knownSha256, move);
    }

    /// <summary>The caller has the digest already (the downloader hashed the file while it arrived), so nothing is read twice.</summary>
    private static StoredImage StoreKnown(string directory, FileInfo source, string? originalFileName, string sha256, bool move)
    {
        var existing = ExistingImage(directory, sha256);
        if (existing is not null)
        {
            if (new FileInfo(existing).Length != source.Length)
            {
                throw new InvalidDataException("An image with this hash is stored with a different size; the hash cannot be right.");
            }

            DiscardSource(source, existing, move);
            return new StoredImage(sha256, source.Length, existing, AlreadyPresent: true);
        }

        var target = Path.Combine(directory, LibraryFiles.ImageName(sha256, originalFileName));
        var temp = Path.Combine(directory, LibraryFiles.TemporaryName(Path.GetFileName(target)));

        try
        {
            if (move)
            {
                File.Move(source.FullName, temp);
            }
            else
            {
                File.Copy(source.FullName, temp);
            }

            return Publish(temp, target, sha256, source.Length);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    private static async Task<StoredImage> StoreUnknownAsync(string directory, FileInfo source, string? originalFileName, bool move, CancellationToken cancellationToken)
    {
        var temp = Path.Combine(directory, LibraryFiles.IncomingName());

        try
        {
            var (sha256, length) = await CopyHashingAsync(source.FullName, temp, cancellationToken).ConfigureAwait(false);

            var existing = ExistingImage(directory, sha256);
            if (existing is not null)
            {
                File.Delete(temp);
                DiscardSource(source, existing, move);
                return new StoredImage(sha256, length, existing, AlreadyPresent: true);
            }

            var stored = Publish(temp, Path.Combine(directory, LibraryFiles.ImageName(sha256, originalFileName)), sha256, length);
            DiscardSource(source, stored.Path, move);
            return stored;
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    /// <summary>
    /// Renames the finished temporary file into place. A file that already has the name and no metadata is a leftover
    /// whose content nobody checked, so it is replaced by the verified one; a concurrent writer of the same hash puts
    /// identical bytes there, which makes the replacement harmless.
    /// </summary>
    private static StoredImage Publish(string temp, string target, string sha256, long length)
    {
        File.Move(temp, target, overwrite: true);
        return new StoredImage(sha256, length, target, AlreadyPresent: false);
    }

    /// <summary>
    /// The stored image of an indexed entry, under any extension: the extension comes from the first file name an
    /// image was added with, and the same content must not be stored twice because a later download was named
    /// differently. An image without metadata does not count, since nothing vouches for its content.
    /// </summary>
    private static string? ExistingImage(string directory, string sha256)
    {
        if (!File.Exists(Path.Combine(directory, LibraryFiles.MetadataName(sha256))))
        {
            return null;
        }

        return Directory.EnumerateFiles(directory, sha256 + ".*").FirstOrDefault(f => LibraryFiles.IsImageNameFor(Path.GetFileName(f), sha256));
    }

    /// <summary>A moved duplicate is dropped from where it came from, unless that is the stored image itself.</summary>
    private static void DiscardSource(FileInfo source, string stored, bool move)
    {
        if (move && !string.Equals(Path.GetFullPath(stored), source.FullName, StringComparison.Ordinal))
        {
            source.Delete();
        }
    }

    private static async Task<(string Sha256, long Length)> CopyHashingAsync(string source, string destination, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

        try
        {
            var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (input.ConfigureAwait(false))
            {
                var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using (output.ConfigureAwait(false))
                {
                    long total = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        total += read;
                    }

                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                    return (Convert.ToHexStringLower(hash.GetHashAndReset()), total);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
