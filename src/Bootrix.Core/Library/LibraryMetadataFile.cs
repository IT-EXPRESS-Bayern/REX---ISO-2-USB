// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bootrix.Core.Json;

namespace Bootrix.Core.Library;

internal static class LibraryMetadataFile
{
    /// <summary>A metadata file is a few hundred bytes; anything bigger is not one, and reading it would only cost memory.</summary>
    public const long MaxBytes = 64 * 1024;

    /// <exception cref="JsonException">The file is not valid JSON of the expected shape.</exception>
    /// <exception cref="InvalidDataException">The file is larger than any metadata file can be.</exception>
    public static async Task<LibraryMetadata> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length > MaxBytes)
        {
            throw new InvalidDataException("the metadata file is unreasonably large");
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Deserialize<LibraryMetadata>(bytes, CoreJson.Options)
            ?? throw new JsonException("the metadata file is empty");
    }

    /// <summary>
    /// Written next to the target and renamed over it, so a reader on this or another machine sees the old or the
    /// new content, never half of it. The data is flushed to disk before the rename, so a power loss cannot leave the
    /// new name pointing at an empty file.
    /// </summary>
    public static async Task WriteAsync(string directory, LibraryMetadata metadata, CancellationToken cancellationToken)
    {
        var target = Path.Combine(directory, LibraryFiles.MetadataName(metadata.Sha256!));
        var temp = Path.Combine(directory, LibraryFiles.TemporaryName(LibraryFiles.MetadataName(metadata.Sha256!)));

        try
        {
            var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer.SerializeAsync(stream, metadata, CoreJson.Options, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }
}
