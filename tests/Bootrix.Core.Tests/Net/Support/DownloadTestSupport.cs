// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using Bootrix.Core.Net;

namespace Bootrix.Core.Tests.Net.Support;

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bootrix-dl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A handle that is still closing must not fail an otherwise passed test.
        }
    }
}

internal static class DownloadTestSupport
{
    public const int MiB = 1024 * 1024;

    public static byte[] RandomBytes(int length, int seed = 1)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    public static string Sha256Hex(byte[] data) => Digest(HashAlgorithmName.SHA256, data);

    public static string Digest(HashAlgorithmName algorithm, ReadOnlySpan<byte> data)
    {
        using var hash = IncrementalHash.CreateHash(algorithm);
        hash.AppendData(data);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Small segments and short back-off so that tests with a few MiB still exercise splitting and retries quickly.</summary>
    public static DownloadOptions FastOptions(int maxSegments = 4, long minSegmentSize = 256 * 1024) => new()
    {
        MaxSegments = maxSegments,
        MinSegmentSize = minSegmentSize,
        RetryBaseDelay = TimeSpan.FromMilliseconds(5),
        RetryMaxDelay = TimeSpan.FromMilliseconds(40),
        StateSaveInterval = TimeSpan.FromMilliseconds(50),
        ProgressInterval = TimeSpan.FromMilliseconds(20),
        ResponseTimeout = TimeSpan.FromSeconds(10),
        StallTimeout = TimeSpan.FromSeconds(10),
    };

    /// <summary>A downloader that bypasses any system proxy, so loopback traffic is never diverted.</summary>
    public static SegmentedDownloader Downloader() => new(handlerFactory: options => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        MaxConnectionsPerServer = options.MaxSegments,
    });

    public static IReadOnlyList<PieceHash> PiecesOf(byte[] content, int pieceLength)
    {
        var digests = new List<string>();
        for (var offset = 0; offset < content.Length; offset += pieceLength)
        {
            digests.Add(Digest(HashAlgorithmName.SHA1, content.AsSpan(offset, Math.Min(pieceLength, content.Length - offset))));
        }

        return PieceHash.CreateUniform(content.Length, pieceLength, HashKind.Sha1, digests);
    }

    public static IReadOnlyList<FileHash> HashesOf(byte[] content) =>
    [
        new FileHash(HashKind.Sha256, Digest(HashAlgorithmName.SHA256, content)),
        new FileHash(HashKind.Sha1, Digest(HashAlgorithmName.SHA1, content)),
        new FileHash(HashKind.Sha512, Digest(HashAlgorithmName.SHA512, content)),
    ];
}

/// <summary>Collects progress reports synchronously; <see cref="Progress{T}"/> would post them to the thread pool and reorder them.</summary>
internal sealed class ProgressLog : IProgress<DownloadProgress>
{
    private readonly List<DownloadProgress> _reports = [];

    public Action<DownloadProgress>? OnReport { get; set; }

    public IReadOnlyList<DownloadProgress> Reports
    {
        get
        {
            lock (_reports)
            {
                return [.. _reports];
            }
        }
    }

    public void Report(DownloadProgress value)
    {
        lock (_reports)
        {
            _reports.Add(value);
        }

        OnReport?.Invoke(value);
    }
}
