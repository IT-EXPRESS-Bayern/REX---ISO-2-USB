// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Security.Cryptography;
using Bootrix.Core.Errors;
using Bootrix.Core.Jobs;
using Microsoft.Extensions.Logging;

namespace Bootrix.Core.Optical.Reading;

/// <summary>
/// One run of the read loop. The image is written strictly in order, so the hash can be taken on the
/// way and a checkpoint is simply "everything before sector N is final".
/// </summary>
internal sealed class RipSession : IDisposable
{
    private const int SectorSize = SectorMath.SectorSize;

    private readonly ISectorReader _reader;
    private readonly Stream _destination;
    private readonly RipOptions _options;
    private readonly IRipCheckpointStore? _checkpoints;
    private readonly IProgress<RipProgress>? _progress;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly long _sectorCount;
    private readonly List<RipNote> _notes;
    private readonly BadSectorMap _bad = new();
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly SpeedEstimator _speed;
    private readonly byte[] _buffer;
    private readonly byte[] _scratch = new byte[SectorSize];
    private readonly CancellationToken _cancellation;

    private long _next;
    private long _resumedFrom;
    private long _lastCheckpointTimestamp;
    private long _lastReportTimestamp;
    private bool _speedReduced;

    public RipSession(
        ISectorReader reader,
        Stream destination,
        RipPlan plan,
        RipOptions options,
        IRipCheckpointStore? checkpoints,
        IProgress<RipProgress>? progress,
        ILogger log,
        TimeProvider time,
        CancellationToken cancellation)
    {
        _reader = reader;
        _destination = destination;
        _options = options;
        _checkpoints = checkpoints;
        _progress = progress;
        _log = log;
        _time = time;
        _cancellation = cancellation;
        _sectorCount = plan.SectorCount;
        _notes = [.. plan.Notes];
        _speed = new SpeedEstimator(time);
        _buffer = new byte[Math.Max(options.ChunkSectors, 1) * SectorSize];
    }

    public RipReport Run(RipPlan plan)
    {
        var started = Stopwatch.GetTimestamp();
        TryResume();
        if (_next == 0)
        {
            _destination.Position = 0;
        }

        _lastCheckpointTimestamp = _lastReportTimestamp = _time.GetTimestamp();

        try
        {
            ReadAll();
        }
        catch
        {
            SaveCheckpoint();
            throw;
        }

        Report(RipPhase.Finishing, force: true);
        var expected = _sectorCount * SectorSize;
        if (_destination.Length != expected)
        {
            _destination.SetLength(expected);
        }

        _destination.Flush();
        _checkpoints?.Clear();

        var sha = Convert.ToHexStringLower(_hash.GetHashAndReset());
        _log.LogInformation("Disc read: {Sectors} sectors, {Bad} unreadable, SHA-256 {Hash}", _sectorCount, _bad.Count, sha);
        return new RipReport(_sectorCount, sha, _bad, _resumedFrom, Stopwatch.GetElapsedTime(started), plan.Toc, _notes);
    }

    public void Dispose()
    {
        _hash.Dispose();
    }

    private void ReadAll()
    {
        var chunk = Math.Max(_options.ChunkSectors, 1);
        while (_next < _sectorCount)
        {
            _cancellation.ThrowIfCancellationRequested();
            var count = (int)Math.Min(chunk, _sectorCount - _next);
            ReadChunk(_next, count);

            var bytes = count * SectorSize;
            _hash.AppendData(_buffer.AsSpan()[..bytes]);
            _destination.Write(_buffer.AsSpan()[..bytes]);
            _next += count;

            Report(RipPhase.Reading, force: false);
            SaveCheckpointIfDue();
        }
    }

    // Fast path first: one request per chunk. A failure is retried as a whole, then sector by sector.
    private void ReadChunk(long lba, int count)
    {
        var done = 0;
        while (done < count)
        {
            var result = _reader.Read(lba + done, count - done, _buffer.AsSpan().Slice(done * SectorSize, (count - done) * SectorSize));
            RejectProtected(result, lba + done);
            if (result.SectorsRead > 0)
            {
                done += result.SectorsRead;
                continue;
            }

            done += RetryChunk(lba + done, count - done, done);
        }
    }

    /// <summary>Returns how many sectors at <paramref name="lba"/> were dealt with, readable or not.</summary>
    private int RetryChunk(long lba, int count, int bufferSector)
    {
        _log.LogWarning("Read error at sector {Lba}, retrying", lba);
        SlowDown();

        var area = _buffer.AsSpan().Slice(bufferSector * SectorSize, count * SectorSize);
        for (var attempt = 0; attempt < _options.ChunkRetries; attempt++)
        {
            PauseAndReseek(lba);
            var result = _reader.Read(lba, count, area);
            RejectProtected(result, lba);
            if (result.SectorsRead > 0)
            {
                return result.SectorsRead;
            }
        }

        // The chunk will not come back as a whole; every remaining sector of it is read on its own.
        for (var i = 0; i < count; i++)
        {
            ReadSingle(lba + i, area.Slice(i * SectorSize, SectorSize));
        }

        return count;
    }

    private void ReadSingle(long lba, Span<byte> target)
    {
        for (var attempt = 0; attempt <= _options.SectorRetries; attempt++)
        {
            if (attempt > 0)
            {
                PauseAndReseek(lba);
            }

            var result = _reader.Read(lba, 1, target);
            RejectProtected(result, lba);
            if (result.SectorsRead == 1)
            {
                return;
            }
        }

        target.Clear();
        _bad.Add(lba);
        _log.LogWarning("Sector {Lba} is unreadable and was filled with zeros", lba);
        if (_bad.Count > _options.MaxBadSectors)
        {
            throw new BootrixException(ErrorCode.ReadError, $"more than {_options.MaxBadSectors} unreadable sectors")
            {
                Arguments = [_bad.Ranges[0].Start],
            };
        }
    }

    private static void RejectProtected(SectorReadResult result, long lba)
    {
        if (result.Status == SectorReadStatus.ProtectedContent)
        {
            throw new BootrixException(ErrorCode.CopyProtected, $"drive refused scrambled content at sector {lba}");
        }
    }

    // After an error a drive often sits with its head on the bad spot; moving it away and back is what makes a retry worth anything.
    private void PauseAndReseek(long lba)
    {
        if (_options.RetryDelay > TimeSpan.Zero && _cancellation.WaitHandle.WaitOne(_options.RetryDelay))
        {
            _cancellation.ThrowIfCancellationRequested();
        }

        var far = lba < _reader.SectorCount / 2 ? _reader.SectorCount - 1 : 0;
        _ = _reader.Read(far, 1, _scratch.AsSpan());
    }

    private void SlowDown()
    {
        if (!_speedReduced && _options.ErrorReadSpeedKilobytesPerSecond is { } speed && _reader.TrySetReadSpeed(speed))
        {
            _speedReduced = true;
            _notes.Add(RipNote.SpeedReduced);
        }
    }

    private void TryResume()
    {
        if (!_options.Resume || _checkpoints?.Load() is not { } checkpoint)
        {
            return;
        }

        var matches = checkpoint.SectorCount == _sectorCount
            && checkpoint.NextSector > 0
            && checkpoint.NextSector <= _sectorCount
            && _destination.Length >= checkpoint.NextSector * SectorSize
            && ProbeMatches(checkpoint);
        if (!matches)
        {
            _log.LogWarning("Checkpoint does not match the disc in the drive, starting over");
            _notes.Add(RipNote.CheckpointDiscarded);
            return;
        }

        foreach (var range in checkpoint.BadSectors)
        {
            for (var lba = range.Start; lba < range.End; lba++)
            {
                _bad.Add(lba);
            }
        }

        RehashWrittenPart(checkpoint.NextSector);
        _next = _resumedFrom = checkpoint.NextSector;
        _destination.Position = _next * SectorSize;
        _log.LogInformation("Continuing at sector {Sector}", _next);
    }

    // The disc may have been swapped between runs. Comparing a few sectors with what is already in the file is cheap and catches that.
    private bool ProbeMatches(RipCheckpoint checkpoint)
    {
        var bad = new BadSectorMap(checkpoint.BadSectors);
        var onDisc = new byte[SectorSize];
        var inFile = new byte[SectorSize];
        foreach (var lba in new[] { 0L, checkpoint.NextSector / 2, checkpoint.NextSector - 1 }.Distinct())
        {
            if (bad.Contains(lba))
            {
                continue;
            }

            var result = _reader.Read(lba, 1, _scratch.AsSpan());
            if (result.SectorsRead < 1)
            {
                continue;
            }

            _scratch.AsSpan().CopyTo(onDisc);
            _destination.Position = lba * SectorSize;
            _destination.ReadExactly(inFile);
            if (!onDisc.AsSpan().SequenceEqual(inFile))
            {
                return false;
            }
        }

        return true;
    }

    private void RehashWrittenPart(long sectors)
    {
        _destination.Position = 0;
        var chunk = new byte[SectorSize * 256];
        long done = 0;
        var total = sectors * SectorSize;
        while (done < total)
        {
            _cancellation.ThrowIfCancellationRequested();
            var take = (int)Math.Min(chunk.Length, total - done);
            _destination.ReadExactly(chunk.AsSpan(0, take));
            _hash.AppendData(chunk.AsSpan(0, take));
            done += take;
            _progress?.Report(new RipProgress(RipPhase.Resuming, done / SectorSize, _sectorCount, _bad.Count, 0, null));
        }
    }

    private void Report(RipPhase phase, bool force)
    {
        if (_progress is null)
        {
            return;
        }

        var now = _time.GetTimestamp();
        if (!force && _time.GetElapsedTime(_lastReportTimestamp, now) < TimeSpan.FromMilliseconds(200))
        {
            return;
        }

        _lastReportTimestamp = now;
        var written = (_next - _resumedFrom) * SectorSize;
        _speed.Update(written);
        _progress.Report(new RipProgress(
            phase,
            _next,
            _sectorCount,
            _bad.Count,
            _speed.BytesPerSecond,
            _speed.Remaining(written, (_sectorCount - _resumedFrom) * SectorSize)));
    }

    private void SaveCheckpointIfDue()
    {
        if (_checkpoints is null || _time.GetElapsedTime(_lastCheckpointTimestamp) < _options.CheckpointInterval)
        {
            return;
        }

        SaveCheckpoint();
    }

    private void SaveCheckpoint()
    {
        if (_checkpoints is null || _next == 0)
        {
            return;
        }

        try
        {
            // The data must be on disk before the checkpoint claims it is.
            _destination.Flush();
            _checkpoints.Save(new RipCheckpoint { SectorCount = _sectorCount, NextSector = _next, BadSectors = [.. _bad.Ranges] });
            _lastCheckpointTimestamp = _time.GetTimestamp();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _log.LogWarning(ex, "Checkpoint could not be written");
        }
    }
}
