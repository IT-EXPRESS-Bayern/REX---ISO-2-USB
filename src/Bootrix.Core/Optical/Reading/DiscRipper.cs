// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Core.Optical.Reading;

/// <param name="SectorCount">Sectors the image will have.</param>
public sealed record RipPlan(long SectorCount, DiscToc? Toc, IReadOnlyList<RipNote> Notes)
{
    public long Bytes => SectorMath.ToBytes(SectorCount);
}

/// <summary>
/// Copies a data disc into an ISO image the way a rescue tool would. Where other tools stop at the first
/// error, a failed read is retried, then repeated sector by sector, and what stays unreadable is
/// zero-filled and listed in the report. A rip can be continued after it was cancelled or the disc was
/// ejected. Discs that cannot be an ISO (audio, mixed mode) and copy-protected ones are refused up front.
/// </summary>
public sealed class DiscRipper(ILogger<DiscRipper>? logger = null, TimeProvider? timeProvider = null)
{
    private readonly ILogger _log = logger ?? NullLogger<DiscRipper>.Instance;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>Looks at the disc and decides what to copy, or refuses it. Reads a handful of sectors at most.</summary>
    public Task<RipPlan> PlanAsync(ISectorReader reader, RipOptions options, CancellationToken cancellationToken = default) =>
        Task.Factory.StartNew(() => Plan(reader, options), cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    public async Task<RipReport> RipAsync(
        ISectorReader reader,
        Stream destination,
        RipOptions options,
        IRipCheckpointStore? checkpoints = null,
        IProgress<RipProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var plan = await PlanAsync(reader, options, cancellationToken).ConfigureAwait(false);
        return await RipAsync(reader, destination, plan, options, checkpoints, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Copies the disc according to a plan made earlier, for callers that show the plan to the user first.</summary>
    public Task<RipReport> RipAsync(
        ISectorReader reader,
        Stream destination,
        RipPlan plan,
        RipOptions options,
        IRipCheckpointStore? checkpoints = null,
        IProgress<RipProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Factory.StartNew(
            () =>
            {
                using var session = new RipSession(reader, destination, plan, options, checkpoints, progress, _log, _time, cancellationToken);
                return session.Run(plan);
            },
            cancellationToken,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

    /// <summary>Plans, checks that the target volume has room, then reads into <paramref name="path"/>; the checkpoint lives beside it.</summary>
    public async Task<RipReport> RipToFileAsync(
        ISectorReader reader,
        string path,
        RipOptions options,
        IProgress<RipProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var plan = await PlanAsync(reader, options, cancellationToken).ConfigureAwait(false);
        var existing = File.Exists(path) ? new FileInfo(path).Length : 0;
        EnsureSpace(path, plan.Bytes - Math.Min(existing, plan.Bytes));

        await using var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        var checkpoints = new FileRipCheckpointStore(options.CheckpointPath ?? path + ".btxrip");
        return await RipAsync(reader, file, plan, options, checkpoints, progress, cancellationToken).ConfigureAwait(false);
    }

    private RipPlan Plan(ISectorReader reader, RipOptions options)
    {
        var notes = new List<RipNote>();
        DiscToc? toc = null;
        if (reader is IDiscInspector inspector)
        {
            toc = inspector.ReadToc();
            RejectUnsupportedContent(toc);
            if (toc is { IsMultiSession: true })
            {
                notes.Add(RipNote.MultiSession);
            }
        }

        if (CopyProtectionDetector.Inspect(reader, _log) is { } finding)
        {
            throw new BootrixException(ErrorCode.CopyProtected, finding.Reason) { Arguments = [finding.System.ToString()] };
        }

        var capacity = reader.SectorCount;
        var volume = Iso9660Probe.ReadVolumeSectors(reader);
        if (capacity <= 0)
        {
            // Some drives report a single sector for rewritable media; the file system is then the only size there is.
            capacity = volume ?? throw new BootrixException(ErrorCode.DeviceNotFound, $"{reader.Name} reports no capacity");
            notes.Add(RipNote.CapacityFromFileSystem);
        }
        else if (options.TrimToFileSystem && volume is { } sectors && sectors < capacity)
        {
            capacity = sectors;
            notes.Add(RipNote.TrimmedToFileSystem);
        }

        return new RipPlan(capacity, toc, notes);
    }

    private static void RejectUnsupportedContent(DiscToc? toc)
    {
        switch (toc?.Content)
        {
            case DiscContent.Audio:
                throw new BootrixException(ErrorCode.AudioDiscNotSupported, "all tracks are audio");
            case DiscContent.Mixed:
                throw new BootrixException(ErrorCode.MixedModeDiscNotSupported, "disc has audio and data tracks");
        }
    }

    private static void EnsureSpace(string path, long neededBytes)
    {
        if (neededBytes <= 0)
        {
            return;
        }

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (root is null)
            {
                return;
            }

            var free = new DriveInfo(root).AvailableFreeSpace;
            if (free < neededBytes)
            {
                throw new BootrixException(ErrorCode.InsufficientSpace, path) { Arguments = [root, SectorMath.FormatBytes(neededBytes)] };
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Network shares and unusual volumes cannot always be asked; the write will fail on its own if there is no room.
        }
    }
}
