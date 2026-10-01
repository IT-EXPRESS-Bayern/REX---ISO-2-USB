// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Bootrix.Core.Errors;
using Bootrix.Core.Jobs;
using Bootrix.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Core.Engine;

/// <summary>
/// Looking at the disks needs no administrator rights, so that part is served by an engine in this
/// process. The privileged part (fingerprints, writing) is only started when it is first needed,
/// which is what finally shows the elevation prompt, and started again when its connection broke.
/// </summary>
public sealed class DeferredEngine : IEngine, IAsyncDisposable
{
    private readonly IEngine _unprivileged;
    private readonly Func<CancellationToken, Task<IEngine>> _startPrivileged;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IEngine? _privileged;

    /// <param name="unprivileged">Serves <see cref="ListDisksAsync"/> and <see cref="DevicesChanged"/>.</param>
    /// <param name="startPrivileged">Creates the engine for everything else; may show a consent prompt and may throw <see cref="BootrixException"/>.</param>
    public DeferredEngine(IEngine unprivileged, Func<CancellationToken, Task<IEngine>> startPrivileged, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(unprivileged);
        ArgumentNullException.ThrowIfNull(startPrivileged);
        _unprivileged = unprivileged;
        _startPrivileged = startPrivileged;
        _logger = logger ?? NullLogger.Instance;
    }

    public event EventHandler? DevicesChanged
    {
        add => _unprivileged.DevicesChanged += value;
        remove => _unprivileged.DevicesChanged -= value;
    }

    /// <summary>True once the privileged engine was started and is still connected.</summary>
    public bool PrivilegedRunning => _privileged is not (null or IRemoteEngine { IsConnected: false });

    public Task<IReadOnlyList<StorageDevice>> ListDisksAsync(DiskFilter filter, CancellationToken cancellationToken) =>
        _unprivileged.ListDisksAsync(filter, cancellationToken);

    public async Task<DiskIdentity> CaptureIdentityAsync(string devicePath, CancellationToken cancellationToken)
    {
        var engine = await GetPrivilegedAsync(cancellationToken).ConfigureAwait(false);
        return await engine.CaptureIdentityAsync(devicePath, cancellationToken).ConfigureAwait(false);
    }

    public async Task<EngineJobResult> RunJobAsync(
        EngineJobRequest request,
        IProgress<ProgressReport> progress,
        CancellationToken cancellationToken,
        CancellationToken abortToken = default)
    {
        IEngine engine;
        var started = Stopwatch.GetTimestamp();
        try
        {
            engine = await GetPrivilegedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new EngineJobResult { Outcome = JobOutcome.Canceled, Duration = Stopwatch.GetElapsedTime(started) };
        }
        catch (BootrixException ex)
        {
            // A refused consent prompt is an outcome of the job like any other, not a crash of the caller.
            return EngineJobResult.From(new JobResult(JobOutcome.Failed, Stopwatch.GetElapsedTime(started), ex));
        }

        return await engine.RunJobAsync(request, progress, cancellationToken, abortToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DiscardAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async Task<IEngine> GetPrivilegedAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_privileged is IRemoteEngine { IsConnected: false })
            {
                _logger.LogInformation("The privileged engine lost its connection and is started again");
                await DiscardAsync().ConfigureAwait(false);
            }

            return _privileged ??= await _startPrivileged(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DiscardAsync()
    {
        var engine = _privileged;
        _privileged = null;
        if (engine is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync().ConfigureAwait(false);
        }
    }
}
