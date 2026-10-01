// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.Logging;

namespace Bootrix.Core.Jobs;

/// <summary>
/// Shared state of a running job: progress reporting, logging, values passed between steps and
/// the cleanup actions that must run when the job ends, whatever the outcome.
/// </summary>
public sealed class JobContext
{
    private readonly Action<double, long, long, string?> _stepProgress;
    private readonly Stack<Func<ValueTask>> _cleanups = new();
    private readonly Dictionary<string, object?> _values = [];

    internal JobContext(
        IJob job,
        ILogger logger,
        Action<double, long, long, string?> stepProgress,
        CancellationToken abortToken)
    {
        Job = job;
        Log = logger;
        AbortToken = abortToken;
        _stepProgress = stepProgress;
    }

    public IJob Job { get; }

    public ILogger Log { get; }

    /// <summary>
    /// Cancelled only when the user insists on an immediate stop. The normal cancellation token
    /// asks steps to finish at the next safe point; this one is for code that is already unwinding.
    /// </summary>
    public CancellationToken AbortToken { get; }

    public void ReportStep(double fraction, long bytesDone = 0, long bytesTotal = 0, string? detail = null)
    {
        _stepProgress(Math.Clamp(fraction, 0, 1), bytesDone, bytesTotal, detail);
    }

    public void ReportBytes(long bytesDone, long bytesTotal, string? detail = null)
    {
        var fraction = bytesTotal > 0 ? (double)bytesDone / bytesTotal : 0;
        _stepProgress(fraction, bytesDone, bytesTotal, detail);
    }

    public void Set<T>(string key, T value) => _values[key] = value;

    public T Get<T>(string key)
    {
        if (_values.TryGetValue(key, out var value) && value is T typed)
        {
            return typed;
        }

        throw new KeyNotFoundException($"Job value '{key}' is not set or has a different type.");
    }

    public bool TryGet<T>(string key, out T? value)
    {
        if (_values.TryGetValue(key, out var raw) && raw is T typed)
        {
            value = typed;
            return true;
        }

        value = default;
        return false;
    }

    public void OnCleanup(Func<ValueTask> cleanup) => _cleanups.Push(cleanup);

    public void OnCleanup(Action cleanup) => _cleanups.Push(() =>
    {
        cleanup();
        return ValueTask.CompletedTask;
    });

    public void OnCleanup(IAsyncDisposable disposable) => _cleanups.Push(disposable.DisposeAsync);

    public void OnCleanup(IDisposable disposable) => OnCleanup(disposable.Dispose);

    internal async Task RunCleanupsAsync()
    {
        while (_cleanups.TryPop(out var cleanup))
        {
            try
            {
                await cleanup().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A failing cleanup must not hide the original error or stop the remaining cleanups.
                Log.LogWarning(ex, "Cleanup action failed");
            }
        }
    }
}
