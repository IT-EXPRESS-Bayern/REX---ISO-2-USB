// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Net;
using Microsoft.Extensions.Logging;

namespace Bootrix.Core.Library;

/// <summary>
/// A content-addressed store for downloaded images. Each image lives in the library folder as
/// <c>&lt;sha256&gt;.&lt;ext&gt;</c> with a sidecar <c>&lt;sha256&gt;.json</c> that says where it came from. A second, read-only
/// folder (a network share) is indexed the same way, so an image that a colleague already downloaded is found before
/// the internet is asked.
/// </summary>
/// <remarks>
/// The index is the folders themselves and is checked on every read, never trusted: a deleted image, a cut-off
/// download or a damaged metadata file shows up as a <see cref="LibraryProblem"/> instead of as an entry that fails later.
/// Everything that writes goes to the local folder only, and runs one at a time within a process.
/// </remarks>
public sealed class ImageLibrary : IDisposable
{
    /// <summary>Temporary files younger than this may belong to a write that is still running, here or on another machine.</summary>
    private static readonly TimeSpan AbandonedAfter = TimeSpan.FromHours(1);

    private readonly string _local;
    private readonly string? _shared;
    private readonly ILogger<ImageLibrary> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _writes = new(1, 1);

    public ImageLibrary(ImageLibraryOptions options, ILogger<ImageLibrary> logger, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.LocalDirectory);

        _local = Path.GetFullPath(options.LocalDirectory);
        _shared = string.IsNullOrWhiteSpace(options.SharedDirectory) ? null : Path.GetFullPath(options.SharedDirectory);
        if (_shared is not null && SameDirectory(_local, _shared))
        {
            throw new ArgumentException("The shared folder must not be the local one: it is only read, and the local one is written.", nameof(options));
        }

        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public void Dispose() => _writes.Dispose();

    public async Task<LibraryListing> ListAsync(CancellationToken cancellationToken)
    {
        var (entries, problems) = await LibraryScanner.ScanAsync(_local, LibraryLocation.Local, cancellationToken).ConfigureAwait(false);

        if (_shared is not null)
        {
            var (sharedEntries, sharedProblems) = await LibraryScanner.ScanAsync(_shared, LibraryLocation.Shared, cancellationToken).ConfigureAwait(false);
            entries.AddRange(sharedEntries);
            problems.AddRange(sharedProblems);
        }

        foreach (var problem in problems)
        {
            _logger.LogDebug("Library problem in {Path}: {Kind}, {Detail}", problem.Path, problem.Kind, problem.Detail);
        }

        return new LibraryListing(entries, problems);
    }

    /// <summary>The image with this SHA-256, from the local folder if it is there, otherwise from the shared one.</summary>
    public async Task<LibraryEntry?> FindAsync(string sha256, CancellationToken cancellationToken)
    {
        var hash = NormalizeHash(sha256);

        var local = await LibraryScanner.FindAsync(_local, LibraryLocation.Local, hash, cancellationToken).ConfigureAwait(false);
        if (local is not null || _shared is null)
        {
            return local;
        }

        return await LibraryScanner.FindAsync(_shared, LibraryLocation.Shared, hash, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Entries whose name, version, product, architecture, language or hash start match every word of the query.</summary>
    public async Task<IReadOnlyList<LibraryEntry>> SearchAsync(string query, CancellationToken cancellationToken) =>
        LibrarySearch.Filter((await ListAsync(cancellationToken).ConfigureAwait(false)).Entries, query);

    /// <summary>
    /// Stores an image under its SHA-256. If the library has it already, nothing is stored a second time; the metadata
    /// is brought up to date instead and <see cref="LibraryAddResult.AlreadyPresent"/> says so.
    /// </summary>
    /// <param name="knownSha256">
    /// The digest, if the caller computed it already (a finished download carries it). It is taken as given, which
    /// saves reading a multi-gigabyte file a second time; <see cref="VerifyAsync"/> is the way to check later. Null makes
    /// the library hash the file while copying it.
    /// </param>
    public async Task<LibraryAddResult> AddAsync(
        string path,
        LibraryImageInfo info,
        LibraryAddMode mode = LibraryAddMode.Copy,
        string? knownSha256 = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(info);
        var known = knownSha256 is null ? null : NormalizeHash(knownSha256);

        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var stored = await LibraryImporter.StoreAsync(_local, path, info.FileName ?? Path.GetFileName(path), known, mode == LibraryAddMode.Move, cancellationToken).ConfigureAwait(false);
            var now = _time.GetUtcNow();
            var previous = stored.AlreadyPresent
                ? await LibraryScanner.FindAsync(_local, LibraryLocation.Local, stored.Sha256, cancellationToken).ConfigureAwait(false)
                : null;

            var metadata = LibraryMetadata.For(stored.Sha256, Path.GetFileName(stored.Path), stored.Size, Merge(previous?.Info, info), previous?.DownloadedUtc ?? now, now);
            await LibraryMetadataFile.WriteAsync(_local, metadata, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("{Outcome} {Sha256} ({Size} bytes) in the image library", stored.AlreadyPresent ? "Refreshed" : "Added", stored.Sha256, stored.Size);

            return new LibraryAddResult(
                new LibraryEntry
                {
                    Sha256 = stored.Sha256,
                    Size = stored.Size,
                    Path = stored.Path,
                    Location = LibraryLocation.Local,
                    Info = metadata.ToInfo(),
                    DownloadedUtc = metadata.DownloadedUtc!.Value,
                    LastUsedUtc = now,
                },
                stored.AlreadyPresent);
        }
        finally
        {
            _writes.Release();
        }
    }

    /// <summary>Records that the image was used now, which is what keeps it from being the first to go in a cleanup. Only local images are tracked.</summary>
    /// <returns>False if the local folder has no such image.</returns>
    public async Task<bool> TouchAsync(string sha256, CancellationToken cancellationToken)
    {
        var hash = NormalizeHash(sha256);

        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entry = await LibraryScanner.FindAsync(_local, LibraryLocation.Local, hash, cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                return false;
            }

            var metadata = LibraryMetadata.For(hash, Path.GetFileName(entry.Path), entry.Size, entry.Info, entry.DownloadedUtc, _time.GetUtcNow());
            await LibraryMetadataFile.WriteAsync(_local, metadata, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _writes.Release();
        }
    }

    /// <summary>Deletes a local image and its metadata. The shared folder is never touched.</summary>
    /// <returns>False if the local folder has no such image.</returns>
    public async Task<bool> RemoveAsync(string sha256, CancellationToken cancellationToken)
    {
        var hash = NormalizeHash(sha256);

        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entry = await LibraryScanner.FindAsync(_local, LibraryLocation.Local, hash, cancellationToken).ConfigureAwait(false);
            if (entry is null)
            {
                return false;
            }

            DeleteLocal(entry);
            return true;
        }
        finally
        {
            _writes.Release();
        }
    }

    public async Task<IReadOnlyList<LibraryDuplicate>> FindDuplicatesAsync(CancellationToken cancellationToken)
    {
        var listing = await ListAsync(cancellationToken).ConfigureAwait(false);

        return
        [
            .. listing.Entries
                .GroupBy(e => e.Sha256, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => new LibraryDuplicate(g.Key, [.. g.OrderBy(e => e.Location)])),
        ];
    }

    /// <summary>What a cleanup under this policy would remove. Nothing is deleted; show the plan, then pass it to <see cref="CleanupAsync"/>.</summary>
    public async Task<LibraryCleanupPlan> PlanCleanupAsync(LibraryCleanupPolicy policy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (policy.MaxTotalBytes < 0 || policy.KeepVersionsPerProduct < 1 || policy.MaxIdle < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "Limits must not be negative, and at least one version has to be kept.");
        }

        var listing = await ListAsync(cancellationToken).ConfigureAwait(false);
        var local = listing.Entries.Where(e => e.Location == LibraryLocation.Local).ToList();
        var shared = listing.Entries.Where(e => e.Location == LibraryLocation.Shared).Select(e => e.Sha256).ToHashSet(StringComparer.Ordinal);

        return LibraryCleanupPlanner.Plan(local, shared, policy, _time.GetUtcNow());
    }

    /// <summary>
    /// Removes what the plan lists. Each image is looked at again first: one that has changed since the preview is left
    /// alone and reported, and so is one that cannot be deleted because a program has it open.
    /// </summary>
    public async Task<LibraryCleanupResult> CleanupAsync(LibraryCleanupPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var removed = new List<LibraryEntry>();
        var failed = new List<LibraryCleanupFailure>();

        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var item in plan.Items.Where(i => i.Entry.Location == LibraryLocation.Local))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var current = await LibraryScanner.FindAsync(_local, LibraryLocation.Local, item.Entry.Sha256, cancellationToken).ConfigureAwait(false);
                if (current is null)
                {
                    continue;
                }

                if (current.Size != item.Entry.Size)
                {
                    failed.Add(new LibraryCleanupFailure(item.Entry, "the image changed after the preview"));
                    continue;
                }

                try
                {
                    DeleteLocal(current);
                    removed.Add(current);
                    _logger.LogInformation("Removed {Sha256} from the image library: {Reason}", current.Sha256, item.Reason);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failed.Add(new LibraryCleanupFailure(item.Entry, ex.Message));
                }
            }
        }
        finally
        {
            _writes.Release();
        }

        return new LibraryCleanupResult(removed, failed, removed.Sum(e => e.Size));
    }

    /// <summary>
    /// Reads the image again and compares it with its address: the check against bit rot and against a file that was
    /// swapped under its name. Works for shared images too, since it only reads.
    /// </summary>
    public async Task<LibraryVerifyOutcome> VerifyAsync(LibraryEntry entry, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var directory = Path.GetDirectoryName(Path.GetFullPath(entry.Path))!;
        if (!SameDirectory(directory, _local) && (_shared is null || !SameDirectory(directory, _shared)))
        {
            throw new ArgumentException("The entry does not belong to this library.", nameof(entry));
        }

        var file = new FileInfo(entry.Path);
        if (!file.Exists)
        {
            return LibraryVerifyOutcome.Missing;
        }

        if (file.Length != entry.Size)
        {
            return LibraryVerifyOutcome.SizeChanged;
        }

        using var handle = File.OpenHandle(entry.Path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hashes = await FileHasher.ComputeAsync(handle, file.Length, [HashKind.Sha256], progress is null ? null : progress.Report, cancellationToken).ConfigureAwait(false);

        return hashes[0].Hex == entry.Sha256 ? LibraryVerifyOutcome.Ok : LibraryVerifyOutcome.ContentChanged;
    }

    /// <summary>
    /// Clears out what the checks found in the local folder: metadata of images that are gone, images that are cut off
    /// or replaced (their size contradicts the name), and temporary files abandoned by an interrupted write.
    /// Images without metadata are left alone; they may be arriving right now.
    /// </summary>
    public async Task<LibraryRepairResult> RepairAsync(CancellationToken cancellationToken)
    {
        await _writes.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (_, problems) = await LibraryScanner.ScanAsync(_local, LibraryLocation.Local, cancellationToken).ConfigureAwait(false);
            var removed = new List<string>();

            foreach (var problem in problems)
            {
                switch (problem.Kind)
                {
                    case LibraryProblemKind.MissingImage:
                    case LibraryProblemKind.InvalidMetadata:
                    case LibraryProblemKind.UnreadableMetadata:
                        removed.AddRange(Delete(MetadataFor(problem)));
                        break;
                    case LibraryProblemKind.SizeMismatch:
                        removed.AddRange(Delete(problem.Path, MetadataFor(problem)));
                        break;
                }
            }

            removed.AddRange(Delete([.. AbandonedTemporaryFiles()]));
            return new LibraryRepairResult(removed);
        }
        finally
        {
            _writes.Release();
        }
    }

    /// <summary>The metadata that goes with a problem: it is either the file the problem names, or sits next to the image it names.</summary>
    private string MetadataFor(LibraryProblem problem) =>
        LibraryFiles.IsMetadataName(Path.GetFileName(problem.Path))
            ? problem.Path
            : Path.Combine(_local, LibraryFiles.MetadataName(Path.GetFileName(problem.Path)[..64]));

    private IEnumerable<string> AbandonedTemporaryFiles()
    {
        if (!Directory.Exists(_local))
        {
            yield break;
        }

        var limit = _time.GetUtcNow() - AbandonedAfter;
        foreach (var file in Directory.EnumerateFiles(_local).Where(f => LibraryFiles.IsTemporaryName(Path.GetFileName(f))))
        {
            if (File.GetLastWriteTimeUtc(file) < limit)
            {
                yield return file;
            }
        }
    }

    /// <summary>Only files directly inside the local folder are ever deleted here, whatever the caller derived the path from.</summary>
    private List<string> Delete(params string[] paths)
    {
        var deleted = new List<string>();
        foreach (var path in paths.Where(p => File.Exists(p) && SameDirectory(Path.GetDirectoryName(Path.GetFullPath(p))!, _local)))
        {
            try
            {
                File.Delete(path);
                deleted.Add(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not remove {Path} from the image library", path);
            }
        }

        return deleted;
    }

    /// <summary>Image first, metadata second: a crash in between leaves metadata without an image, which the checks report and repair removes, instead of an image nobody knows about.</summary>
    private void DeleteLocal(LibraryEntry entry)
    {
        File.Delete(entry.Path);
        File.Delete(Path.Combine(_local, LibraryFiles.MetadataName(entry.Sha256)));
    }

    private static LibraryImageInfo Merge(LibraryImageInfo? previous, LibraryImageInfo added) => new()
    {
        Name = added.Name ?? previous?.Name,
        Version = added.Version ?? previous?.Version,
        CatalogId = added.CatalogId ?? previous?.CatalogId,
        VariantId = added.VariantId ?? previous?.VariantId,
        Architecture = added.Architecture ?? previous?.Architecture,
        Language = added.Language ?? previous?.Language,
        Source = added.Source ?? previous?.Source,
    };

    private static string NormalizeHash(string sha256)
    {
        ArgumentNullException.ThrowIfNull(sha256);

        var hash = sha256.Trim().ToLowerInvariant();
        return LibraryFiles.IsSha256(hash) ? hash : throw new ArgumentException("A SHA-256 has 64 hex characters.", nameof(sha256));
    }

    private static bool SameDirectory(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(a),
            Path.TrimEndingDirectorySeparator(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
