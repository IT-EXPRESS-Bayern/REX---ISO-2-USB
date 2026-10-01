// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Bootrix.Core.Engine;
using Bootrix.Core.Errors;
using Bootrix.Core.Jobs;
using Bootrix.Core.Storage;
using Bootrix.Core.Writing;
using Bootrix.Core.Writing.Restore;
using Bootrix.Windows.Jobs;
using Bootrix.Windows.Storage;
using Bootrix.Windows.Tiny;
using Bootrix.Windows.Writing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Windows.Engine;

/// <summary>
/// The engine that does the work in the process it runs in. The CLI uses it directly; the broker
/// hosts it for the GUI. Requests carry plain data only, so devices are looked up here and images are
/// opened here, never taken over from the caller.
/// </summary>
public sealed class LocalEngine : IEngine
{
    private readonly IDiskService _disks;
    private readonly JobRunner _runner;
    private readonly Func<StorageDevice, DiskIdentity> _capture;
    private readonly ILogger _logger;
    private readonly Dictionary<Type, JobKind> _kinds;
    private readonly Dictionary<Type, Func<EngineJobRequest, IProgress<ProgressReport>, CancellationToken, CancellationToken, Task<EngineJobResult>>> _selfRunning = [];

    public LocalEngine(
        IDiskService disks,
        JobRunner runner,
        RawWriteJob rawWrite,
        WriteImageJobFactory writeImage,
        RestoreDriveJob restore,
        VerifyMediaJob verify,
        TinyBuildRunner tiny,
        ILogger<LocalEngine>? logger = null)
        : this(disks, runner, rawWrite.Create, DiskIdentityReader.Capture, logger)
    {
        _selfRunning[typeof(TinyBuildJobRequest)] = async (request, progress, cancellationToken, abortToken) =>
            EngineJobResult.From(await tiny.RunAsync((TinyBuildJobRequest)request, new DelegateProgressSink(progress.Report), cancellationToken, abortToken).ConfigureAwait(false));
        _kinds[typeof(WriteImageJobRequest)] = new(
            (request, ct) => writeImage.CreateAsync((WriteImageJobRequest)request, ct),
            SummarizeRawWrite);
        _kinds[typeof(RestoreDriveJobRequest)] = new(
            (request, ct) => CreateRestoreAsync((RestoreDriveJobRequest)request, restore, ct),
            EngineJobResult.From);
        _kinds[typeof(VerifyJobRequest)] = new(
            (request, ct) => CreateVerifyAsync((VerifyJobRequest)request, verify, ct),
            SummarizeVerify);
    }

    internal LocalEngine(
        IDiskService disks,
        JobRunner runner,
        Func<RawWriteRequest, IJob> createRawWrite,
        Func<StorageDevice, DiskIdentity> capture,
        ILogger? logger = null)
    {
        _disks = disks;
        _runner = runner;
        _capture = capture;
        _logger = logger ?? NullLogger.Instance;

        // One entry per kind of request. A new kind of job adds its request type here, how to build the
        // job from it, and how to read its result; the validator of the broker has a matching table.
        _kinds = new Dictionary<Type, JobKind>
        {
            [typeof(RawWriteJobRequest)] = new(
                (request, ct) => CreateRawWriteAsync((RawWriteJobRequest)request, createRawWrite, ct),
                SummarizeRawWrite),
        };
    }

    public event EventHandler? DevicesChanged
    {
        add => _disks.DevicesChanged += value;
        remove => _disks.DevicesChanged -= value;
    }

    public Task<IReadOnlyList<StorageDevice>> ListDisksAsync(DiskFilter filter, CancellationToken cancellationToken) =>
        Task.Run(() => _disks.Enumerate(filter), cancellationToken);

    public async Task<DiskIdentity> CaptureIdentityAsync(string devicePath, CancellationToken cancellationToken)
    {
        var device = await FindAsync(devicePath, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() => _capture(device), cancellationToken).ConfigureAwait(false);
    }

    public async Task<EngineJobResult> RunJobAsync(
        EngineJobRequest request,
        IProgress<ProgressReport> progress,
        CancellationToken cancellationToken,
        CancellationToken abortToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);

        var started = Stopwatch.GetTimestamp();
        if (_selfRunning.TryGetValue(request.GetType(), out var run))
        {
            return await run(request, progress, cancellationToken, abortToken).ConfigureAwait(false);
        }

        if (!_kinds.TryGetValue(request.GetType(), out var kind))
        {
            return Failed(started, new BootrixException(ErrorCode.InvalidSpec, request.GetType().Name) { Arguments = ["request type is not supported"] });
        }

        IJob job;
        try
        {
            job = await kind.CreateAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new EngineJobResult { Outcome = JobOutcome.Canceled, Duration = Stopwatch.GetElapsedTime(started) };
        }
        catch (BootrixException ex)
        {
            _logger.LogWarning("Job could not be set up: {Code}", ex.Code);
            return Failed(started, ex);
        }

        var result = await _runner.RunAsync(job, new DelegateProgressSink(progress.Report), cancellationToken, abortToken).ConfigureAwait(false);
        return kind.Summarize(result);
    }

    internal static EngineJobResult SummarizeRawWrite(JobResult result)
    {
        var summary = EngineJobResult.From(result);
        if (result.Succeeded && result.Values.TryGetValue(RawWriteJob.ReportKey, out var value) && value is RawWriteReport report)
        {
            return summary with { ImageSha256 = report.Sha256, ImageBytes = report.ImageBytes };
        }

        return summary;
    }

    internal static EngineJobResult SummarizeVerify(JobResult result)
    {
        var summary = EngineJobResult.From(result);
        return result.Values.TryGetValue(VerifyMediaJob.BytesKey, out var value) && value is long bytes ? summary with { ImageBytes = bytes } : summary;
    }

    private static EngineJobResult Failed(long started, BootrixException error) =>
        EngineJobResult.From(new JobResult(JobOutcome.Failed, Stopwatch.GetElapsedTime(started), error));

    private async Task<StorageDevice> FindAsync(string devicePath, CancellationToken cancellationToken) =>
        await Task.Run(() => _disks.Find(devicePath), cancellationToken).ConfigureAwait(false)
            ?? throw new BootrixException(ErrorCode.DeviceNotFound, devicePath);

    private async Task<IJob> CreateRawWriteAsync(RawWriteJobRequest request, Func<RawWriteRequest, IJob> create, CancellationToken cancellationToken)
    {
        var targets = new List<RawWriteTargetRequest>(request.Targets.Count);
        foreach (var target in request.Targets)
        {
            var device = await FindAsync(target.DevicePath, cancellationToken).ConfigureAwait(false);
            targets.Add(new RawWriteTargetRequest(device, target.Identity));
        }

        return create(new RawWriteRequest
        {
            ImagePath = request.ImagePath,
            Targets = targets,
            Verify = request.Verify,
            ArchiveEntry = request.ArchiveEntry,
            BlockMap = request.BlockMap,
        });
    }

    private async Task<IJob> CreateRestoreAsync(RestoreDriveJobRequest request, RestoreDriveJob restore, CancellationToken cancellationToken)
    {
        var options = request.ToOptions();
        var targets = new List<RestoreTargetRequest>(request.Targets.Count);
        foreach (var target in request.Targets)
        {
            var device = await FindAsync(target.DevicePath, cancellationToken).ConfigureAwait(false);
            if (device.IsBlocked)
            {
                throw new BootrixException(ErrorCode.DeviceProtected, device.DevicePath) { Arguments = [device.Protection.ToString()] };
            }

            targets.Add(new RestoreTargetRequest(device, target.Identity, RestorePlanner.Plan(options, MediaPlanService.CapsOf(device))));
        }

        return restore.Create(targets);
    }

    private async Task<IJob> CreateVerifyAsync(VerifyJobRequest request, VerifyMediaJob verify, CancellationToken cancellationToken)
    {
        var targets = new List<VerifyTargetRequest>(request.Targets.Count);
        foreach (var target in request.Targets)
        {
            targets.Add(new VerifyTargetRequest(await FindAsync(target.DevicePath, cancellationToken).ConfigureAwait(false), target.Identity));
        }

        return verify.Create(new VerifyMediaRequest
        {
            ImagePath = request.ImagePath,
            Targets = targets,
            Mode = request.Mode,
            ArchiveEntry = request.ArchiveEntry,
            BlockMap = request.BlockMap,
        });
    }

    private sealed record JobKind(Func<EngineJobRequest, CancellationToken, Task<IJob>> CreateAsync, Func<JobResult, EngineJobResult> Summarize);
}
