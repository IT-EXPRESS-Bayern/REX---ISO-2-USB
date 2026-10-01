// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.Diagnostics;
using Bootrix.Core.Errors;
using Bootrix.Core.Ipc;
using Bootrix.Core.Jobs;
using Bootrix.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Core.Engine;

public sealed record BrokerClientOptions
{
    /// <summary>The secret the broker was started with.</summary>
    public byte[]? Secret { get; init; }

    public RpcConnectionOptions Connection { get; init; } = new();
}

/// <summary>
/// The unprivileged end of the broker connection: an <see cref="IEngine"/> whose work is done by the
/// elevated process. A job that is cut off by a lost connection ends with a failed result carrying
/// <see cref="ErrorCode.BrokerDisconnected"/>, and a request the broker refuses with the code of the refusal,
/// so callers handle both like any other job failure.
/// </summary>
public sealed class BrokerEngineClient : IRemoteEngine, IAsyncDisposable
{
    private readonly RpcConnection _connection;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, IProgress<ProgressReport>> _runs = new(StringComparer.Ordinal);

    private BrokerEngineClient(RpcConnection connection, ILogger logger)
    {
        _connection = connection;
        _logger = logger;
    }

    public event EventHandler? DevicesChanged;

    public bool IsConnected => !_connection.IsClosed;

    public Task Disconnected => _connection.Completion;

    /// <summary>Starts the connection on <paramref name="stream"/> and performs the hello. The stream belongs to the client from here on, also when the hello fails.</summary>
    public static async Task<BrokerEngineClient> ConnectAsync(
        Stream stream,
        BrokerClientOptions? options = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new BrokerClientOptions();
        logger ??= NullLogger.Instance;

        var connection = new RpcConnection(stream, options: options.Connection, logger: logger);
        var client = new BrokerEngineClient(connection, logger);
        connection.Handlers
            .AddNotification<ProgressNotification>(BrokerProtocol.Progress, client.OnProgress)
            .AddNotification(BrokerProtocol.DevicesChanged, client.OnDevicesChanged);
        connection.Start();

        try
        {
            var hello = new HelloParams(
                BrokerProtocol.MinimumVersion,
                BrokerProtocol.CurrentVersion,
                AppInfo.Version,
                options.Secret is null ? null : Convert.ToBase64String(options.Secret));
            var answer = await connection.InvokeAsync<HelloResult>(BrokerProtocol.Hello, hello, cancellationToken).ConfigureAwait(false);
            if (answer.Version is < BrokerProtocol.MinimumVersion or > BrokerProtocol.CurrentVersion)
            {
                throw new RpcProtocolException($"the broker chose protocol version {answer.Version}");
            }

            logger.LogInformation("Connected to broker {Build}, protocol version {Version}", answer.Build, answer.Version);
            return client;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<IReadOnlyList<StorageDevice>> ListDisksAsync(DiskFilter filter, CancellationToken cancellationToken) =>
        await _connection.InvokeAsync<List<StorageDevice>>(BrokerProtocol.ListDisks, new ListDisksParams(filter), cancellationToken).ConfigureAwait(false);

    public Task<DiskIdentity> CaptureIdentityAsync(string devicePath, CancellationToken cancellationToken) =>
        _connection.InvokeAsync<DiskIdentity>(BrokerProtocol.CaptureIdentity, new CaptureIdentityParams(devicePath), cancellationToken);

    public async Task<EngineJobResult> RunJobAsync(
        EngineJobRequest request,
        IProgress<ProgressReport> progress,
        CancellationToken cancellationToken,
        CancellationToken abortToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(progress);

        if (cancellationToken.IsCancellationRequested || abortToken.IsCancellationRequested)
        {
            return new EngineJobResult { Outcome = JobOutcome.Canceled };
        }

        var runId = Guid.NewGuid().ToString("N");
        _runs[runId] = progress;
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await _connection.InvokeCooperativeAsync<EngineJobResult>(
                BrokerProtocol.RunJob,
                new RunJobParams(runId, request),
                cancellationToken,
                abortToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new EngineJobResult { Outcome = JobOutcome.Canceled, Duration = Stopwatch.GetElapsedTime(started) };
        }
        catch (BootrixException ex)
        {
            // Every failure of the call, from a lost connection to a refused request, becomes the outcome of the job.
            _logger.LogWarning("Job {RunId} failed on the broker connection with {Code}", runId, ex.Code);
            var canceled = ex is RpcConnectionClosedException && abortToken.IsCancellationRequested;
            return EngineJobResult.From(new JobResult(canceled ? JobOutcome.Canceled : JobOutcome.Failed, Stopwatch.GetElapsedTime(started), ex));
        }
        finally
        {
            _runs.TryRemove(runId, out _);
        }
    }

    /// <summary>Tells the broker that no more work is coming, so it can exit at once instead of waiting out its idle timeout, and closes the connection.</summary>
    public async ValueTask DisposeAsync()
    {
        if (IsConnected)
        {
            try
            {
                await _connection.NotifyAsync(BrokerProtocol.Shutdown).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is RpcConnectionClosedException or RpcProtocolException)
            {
                // Already gone, which is what we wanted.
            }
        }

        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private void OnProgress(ProgressNotification notification)
    {
        if (notification.RunId is not null && _runs.TryGetValue(notification.RunId, out var sink))
        {
            sink.Report(notification.Report);
        }
    }

    private void OnDevicesChanged() => DevicesChanged?.Invoke(this, EventArgs.Empty);
}
