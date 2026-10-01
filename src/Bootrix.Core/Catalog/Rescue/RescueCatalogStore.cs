// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Net;
using Microsoft.Extensions.Logging;

namespace Bootrix.Core.Catalog.Rescue;

public enum RescueCatalogUpdateStatus
{
    /// <summary>A newer catalog was verified and is now in effect.</summary>
    Updated,

    /// <summary>The offered catalog is valid but not newer than the one in effect.</summary>
    UpToDate,
}

public sealed record RescueCatalogUpdate(RescueCatalogUpdateStatus Status, long Version);

/// <summary>
/// Decides which rescue catalog is in effect: the one embedded in the build, or a newer one that came through the
/// signed channel and sits in the cache folder. The cache is never trusted as such. Its file is the signed envelope as
/// it was received, so it is verified again on every start, and a file that fails any check is ignored.
/// </summary>
public sealed class RescueCatalogStore
{
    /// <summary>Manifest channel of the catalog; the rollback counter in the version store is kept under this name.</summary>
    public const string Channel = "rescue-catalog";

    private const string CacheFileName = "rescue-catalog.signed.json";

    private readonly string _cachePath;
    private readonly SignedManifestVerifier? _verifier;
    private readonly ILogger<RescueCatalogStore> _logger;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private RescueCatalogSnapshot? _current;

    /// <param name="verifier">
    /// Checks signed updates. Null while the project has no trusted signing key: the catalog inside the program is used
    /// and every update is refused.
    /// </param>
    public RescueCatalogStore(string cacheDirectory, SignedManifestVerifier? verifier, ILogger<RescueCatalogStore> logger, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        ArgumentNullException.ThrowIfNull(logger);

        _cachePath = Path.Combine(cacheDirectory, CacheFileName);
        _verifier = verifier;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public RescueCatalogSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current ??= Load();
            }
        }
    }

    /// <summary>Reads the cache folder again, for when another process (or the user) changed it.</summary>
    public RescueCatalogSnapshot Reload()
    {
        lock (_gate)
        {
            return _current = Load();
        }
    }

    /// <summary>
    /// Verifies a signed envelope and, if its catalog is newer than the one in effect, keeps it in the cache and
    /// makes it current. Signature, channel, expiry and rollback failures surface as <see cref="ErrorCode.SignatureInvalid"/>,
    /// a signed but malformed catalog as <see cref="ErrorCode.CatalogUnavailable"/>. Nothing changes on failure.
    /// </summary>
    public RescueCatalogUpdate Apply(ReadOnlySpan<byte> envelope)
    {
        var verifier = _verifier ?? throw new BootrixException(ErrorCode.SignatureInvalid, "no trusted signing key is configured");
        var manifest = verifier.Verify(envelope, Channel);
        var document = RescueCatalogReader.Read(manifest.Payload);

        lock (_gate)
        {
            var current = (_current ??= Load()).Document;
            if (document.Version <= current.Version)
            {
                return new RescueCatalogUpdate(RescueCatalogUpdateStatus.UpToDate, current.Version);
            }

            Save(envelope);
            _current = Snapshot(document, RescueCatalogOrigin.Cache);
            return new RescueCatalogUpdate(RescueCatalogUpdateStatus.Updated, document.Version);
        }
    }

    private RescueCatalogSnapshot Load()
    {
        var embedded = EmbeddedRescueCatalog.Document;
        var cached = TryLoadCache();

        return cached is not null && cached.Version > embedded.Version
            ? Snapshot(cached, RescueCatalogOrigin.Cache)
            : Snapshot(embedded, RescueCatalogOrigin.Embedded);
    }

    private RescueCatalogSnapshot Snapshot(RescueCatalogDocument document, RescueCatalogOrigin origin) =>
        new(document, origin, DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime) > document.Expires);

    private RescueCatalogDocument? TryLoadCache()
    {
        if (_verifier is null)
        {
            return null;
        }

        byte[] envelope;
        try
        {
            envelope = File.ReadAllBytes(_cachePath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The cached rescue catalog {Path} cannot be read", _cachePath);
            return null;
        }

        try
        {
            return RescueCatalogReader.Read(_verifier.Verify(envelope, Channel).Payload);
        }
        catch (BootrixException ex)
        {
            _logger.LogWarning(ex, "The cached rescue catalog {Path} is not usable and is ignored", _cachePath);
            return null;
        }
    }

    /// <summary>Written next to the target and renamed over it, so a crash leaves either the old or the new file, never half of one.</summary>
    private void Save(ReadOnlySpan<byte> envelope)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
        var temp = $"{_cachePath}.{Guid.NewGuid():N}.tmp";

        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(envelope);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, _cachePath, overwrite: true);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }
}
