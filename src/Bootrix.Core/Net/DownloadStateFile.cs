// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bootrix.Core.Json;

namespace Bootrix.Core.Net;

internal sealed record DownloadState
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    public required string Url { get; init; }

    public string? ETag { get; init; }

    public DateTimeOffset? LastModified { get; init; }

    public long Length { get; init; }

    public required IReadOnlyList<StateRange> Ranges { get; init; }
}

internal sealed record StateRange(long Start, long End);

/// <summary>
/// The <c>.btxdl</c> file next to the target. It is replaced atomically, so after a crash it holds either the
/// previous or the new list of finished ranges, never a torn mix.
/// </summary>
internal static class DownloadStateFile
{
    public static string PathFor(string destination) => destination + ".btxdl";

    public static string PartPathFor(string destination) => destination + ".part";

    public static async Task WriteAsync(string statePath, DownloadState state, CancellationToken cancellationToken)
    {
        var temp = statePath + ".tmp";

        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
        {
            await JsonSerializer.SerializeAsync(stream, state, CoreJson.Options, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, statePath, overwrite: true);
    }

    /// <summary>Returns null for a missing, unreadable or foreign-version file; the caller then starts from scratch.</summary>
    public static async Task<DownloadState?> TryReadAsync(string statePath, CancellationToken cancellationToken)
    {
        if (!File.Exists(statePath))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(statePath);
            var state = await JsonSerializer.DeserializeAsync<DownloadState>(stream, CoreJson.Options, cancellationToken).ConfigureAwait(false);
            return IsConsistent(state) ? state : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static void Delete(string statePath)
    {
        TryDelete(statePath);
        TryDelete(statePath + ".tmp");
    }

    public static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool IsConsistent(DownloadState? state)
    {
        if (state is not { Version: DownloadState.CurrentVersion, Length: > 0 })
        {
            return false;
        }

        long previousEnd = -1;
        foreach (var range in state.Ranges)
        {
            if (range.Start < 0 || range.End <= range.Start || range.End > state.Length || range.Start < previousEnd)
            {
                return false;
            }

            previousEnd = range.End;
        }

        return true;
    }
}
