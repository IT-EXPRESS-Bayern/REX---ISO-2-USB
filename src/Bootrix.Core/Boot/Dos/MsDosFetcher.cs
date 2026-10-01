// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using Bootrix.Core.Errors;

namespace Bootrix.Core.Boot.Dos;

/// <summary>Checks the Authenticode signature of a downloaded file; the platform layer knows how.</summary>
public interface IMicrosoftSignatureVerifier
{
    bool IsSignedByMicrosoft(string path);
}

/// <summary>Where diskcopy.dll comes from and what it has to look like; the values below are those of the file Microsoft publishes.</summary>
public sealed record MsDosSource(Uri Url, long Length, string Sha256)
{
    public static MsDosSource Microsoft { get; } = new(
        new Uri("https://msdl.microsoft.com/download/symbols/diskcopy.dll/54505118173000/diskcopy.dll"),
        0x16EE00,
        "95fc0786f5bc0a6db5c0604b31ac18fbed0502a2c6858e5fb02a647983ae03c7");
}

/// <summary>
/// Gets diskcopy.dll from Microsoft's symbol server, the file that carries the Windows ME startup disk. Bootrix may not
/// ship those files, so they are fetched on the user's explicit request, accepted only if size, SHA-256 and Microsoft's
/// signature all match, and kept in a folder only administrators can write to.
/// </summary>
public sealed class MsDosFetcher(HttpClient http, IMicrosoftSignatureVerifier verifier, string cacheDirectory, MsDosSource? source = null)
{
    // The symbol server only answers clients that introduce themselves like the debugger tools do.
    public const string UserAgent = "Microsoft-Symbol-Server/10.0.22621.755";

    private const string FileName = "diskcopy.dll";

    private readonly MsDosSource _source = source ?? MsDosSource.Microsoft;

    public string CachedPath => Path.Combine(cacheDirectory, FileName);

    /// <summary>The verified bytes of diskcopy.dll from the cache or, when <paramref name="allowDownload"/> is set, from Microsoft.</summary>
    /// <exception cref="BootrixException"><see cref="ErrorCode.MsDosNotDownloaded"/> when the file is not cached and the user has not agreed to the download.</exception>
    public async Task<byte[]> GetAsync(bool allowDownload, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var cached = await TryReadCachedAsync(cancellationToken).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached;
        }

        if (!allowDownload)
        {
            throw new BootrixException(ErrorCode.MsDosNotDownloaded, "diskcopy.dll is not in the cache and the download was not confirmed");
        }

        Directory.CreateDirectory(cacheDirectory);
        var temporary = CachedPath + ".download";
        try
        {
            await DownloadAsync(temporary, progress, cancellationToken).ConfigureAwait(false);
            var content = await File.ReadAllBytesAsync(temporary, cancellationToken).ConfigureAwait(false);
            Verify(content, temporary);
            File.Move(temporary, CachedPath, overwrite: true);
            return content;
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private async Task<byte[]?> TryReadCachedAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(CachedPath))
        {
            return null;
        }

        var content = await File.ReadAllBytesAsync(CachedPath, cancellationToken).ConfigureAwait(false);
        try
        {
            Verify(content, CachedPath);
            return content;
        }
        catch (BootrixException)
        {
            // Damaged or replaced: fetch it again instead of failing the job.
            File.Delete(CachedPath);
            return null;
        }
    }

    private async Task DownloadAsync(string destination, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _source.Url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } announced && announced != _source.Length)
            {
                throw Untrusted($"the server announces {announced} bytes");
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > _source.Length)
                {
                    throw Untrusted("the file is larger than expected");
                }

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                progress?.Report((double)total / _source.Length);
            }
        }
        catch (HttpRequestException ex)
        {
            throw new BootrixException(ErrorCode.DownloadFailed, ex.Message, ex) { Arguments = [ex.Message] };
        }
    }

    private void Verify(byte[] content, string path)
    {
        if (content.Length != _source.Length)
        {
            throw Untrusted($"{content.Length} bytes instead of {_source.Length}");
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(content));
        if (!string.Equals(hash, _source.Sha256, StringComparison.Ordinal))
        {
            throw Untrusted($"SHA-256 {hash}");
        }

        if (!verifier.IsSignedByMicrosoft(path))
        {
            throw Untrusted("no valid Microsoft signature");
        }
    }

    private static BootrixException Untrusted(string detail) =>
        new(ErrorCode.MsDosFilesUntrusted, $"{FileName}: {detail}") { Arguments = [FileName] };
}
