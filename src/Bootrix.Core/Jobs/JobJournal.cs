// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bootrix.Core.Json;
using Bootrix.Core.Errors;

namespace Bootrix.Core.Jobs;

public enum JournalState
{
    Running,
    Interrupted,
    Completed,
}

public sealed record JournalEntry
{
    public required string JobId { get; init; }

    public required string Kind { get; init; }

    public JournalState State { get; init; } = JournalState.Running;

    /// <summary>Identity string of the target disk (device id, serial and size) used to recognise the stick again.</summary>
    public string? DiskIdentity { get; init; }

    public string? Phase { get; init; }

    public long LastFlushedOffset { get; init; }

    public string? SourceSha256 { get; init; }

    public string? SourcePath { get; init; }

    public DateTimeOffset StartedUtc { get; init; }

    public DateTimeOffset UpdatedUtc { get; init; }
}

/// <summary>
/// One small JSON file per job. It is rewritten atomically so that a power cut leaves either the
/// old or the new version on disk, never a half-written file.
/// </summary>
public sealed class JobJournal(string directory, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public string Directory { get; } = directory;

    private string PathFor(string jobId) => Path.Combine(Directory, jobId + ".json");

    public async Task WriteAsync(JournalEntry entry, CancellationToken cancellationToken = default)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var updated = entry with { UpdatedUtc = _time.GetUtcNow() };
        var target = PathFor(entry.JobId);
        var temp = target + ".tmp";

        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, updated, CoreJson.Options, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, target, overwrite: true);
    }

    public async Task<JournalEntry?> ReadAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var path = PathFor(jobId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<JournalEntry>(stream, CoreJson.Options, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new BootrixException(ErrorCode.JournalCorrupt, path, ex);
        }
    }

    /// <summary>Jobs that were still running when the process or the machine went away.</summary>
    public async Task<IReadOnlyList<JournalEntry>> FindUnfinishedAsync(CancellationToken cancellationToken = default)
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        var result = new List<JournalEntry>();
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*.json"))
        {
            try
            {
                var entry = await ReadAsync(Path.GetFileNameWithoutExtension(file), cancellationToken).ConfigureAwait(false);
                if (entry is { State: JournalState.Running or JournalState.Interrupted })
                {
                    result.Add(entry);
                }
            }
            catch (BootrixException)
            {
                // A damaged journal cannot be resumed; ignore it rather than blocking the start screen.
            }
        }

        return result;
    }

    public Task MarkCompletedAsync(JournalEntry entry, CancellationToken cancellationToken = default) =>
        WriteAsync(entry with { State = JournalState.Completed }, cancellationToken);

    public void Delete(string jobId)
    {
        var path = PathFor(jobId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
