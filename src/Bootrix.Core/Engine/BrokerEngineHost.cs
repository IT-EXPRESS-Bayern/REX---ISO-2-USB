// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Security.Cryptography;
using Bootrix.Core.Errors;
using Bootrix.Core.Ipc;
using Bootrix.Core.Jobs;
using Bootrix.Core.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Core.Engine;

public sealed record BrokerEngineHostOptions
{
    /// <summary>The secret the client has to present in its hello. Null accepts any client, which only makes sense in tests.</summary>
    public byte[]? Secret { get; init; }

    /// <summary>How long a job may take to stop after an abort before <see cref="OnAbortStuck"/> is called.</summary>
    public TimeSpan AbortGrace { get; init; } = TimeSpan.FromSeconds(30);

    public Action? OnAbortStuck { get; init; }

    /// <summary>Called when the client says it is done.</summary>
    public Action? OnShutdownRequested { get; init; }

    /// <summary>A client that does not say hello within this time is dropped, so it cannot hold the only connection of the broker.</summary>
    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;
}

/// <summary>
/// Serves an <see cref="IEngine"/> to one client over an <see cref="RpcConnection"/>. This is the
/// elevated side: nothing is accepted before the hello with the right secret, every request is
/// validated, and a client that disappears in the middle of a job aborts that job so its cleanup runs.
/// </summary>
public sealed class BrokerEngineHost : IDisposable
{
    private readonly IEngine _engine;
    private readonly RpcConnection _connection;
    private readonly BrokerEngineHostOptions _options;
    private readonly ILogger _logger;
    private readonly ITimer _helloTimer;
    private volatile bool _helloDone;

    public BrokerEngineHost(IEngine engine, RpcConnection connection, BrokerEngineHostOptions? options = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(connection);
        _engine = engine;
        _connection = connection;
        _options = options ?? new BrokerEngineHostOptions();
        _logger = logger ?? NullLogger.Instance;

        connection.Handlers
            .Add<HelloParams, HelloResult>(BrokerProtocol.Hello, HelloAsync)
            .Add<ListDisksParams, IReadOnlyList<StorageDevice>>(BrokerProtocol.ListDisks, ListDisksAsync)
            .Add<CaptureIdentityParams, DiskIdentity>(BrokerProtocol.CaptureIdentity, CaptureIdentityAsync)
            .Add<RunJobParams, EngineJobResult>(BrokerProtocol.RunJob, RunJobAsync)
            .AddNotification(BrokerProtocol.Shutdown, OnShutdown);

        _engine.DevicesChanged += OnDevicesChanged;
        _helloTimer = _options.TimeProvider.CreateTimer(_ => OnHelloTimeout(), null, _options.HelloTimeout, Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        _engine.DevicesChanged -= OnDevicesChanged;
        _helloTimer.Dispose();
    }

    private Task<HelloResult> HelloAsync(HelloParams hello, RpcCallContext context)
    {
        if (_helloDone)
        {
            throw new RpcFatalException("hello was sent twice");
        }

        if (BrokerProtocol.Negotiate(hello.MinVersion, hello.MaxVersion) is not { } version)
        {
            throw new RpcFatalException(
                $"protocol versions {hello.MinVersion}-{hello.MaxVersion} do not match {BrokerProtocol.MinimumVersion}-{BrokerProtocol.CurrentVersion}");
        }

        if (!SecretMatches(hello.Secret))
        {
            throw new RpcFatalException("authentication failed");
        }

        _helloDone = true;
        _helloTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        var build = hello.Build ?? "";
        _logger.LogInformation("Client {Build} connected, protocol version {Version}", build.Length > 32 ? build[..32] : build, version);
        return Task.FromResult(new HelloResult(version, AppInfo.Version));
    }

    private bool SecretMatches(string? presented)
    {
        if (_options.Secret is not { } expected)
        {
            return true;
        }

        try
        {
            return presented is not null && CryptographicOperations.FixedTimeEquals(expected, Convert.FromBase64String(presented));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private async Task<IReadOnlyList<StorageDevice>> ListDisksAsync(ListDisksParams request, RpcCallContext context)
    {
        RequireHello();
        return await _engine.ListDisksAsync(request.Filter ?? new DiskFilter(), context.CancellationToken).ConfigureAwait(false);
    }

    private async Task<DiskIdentity> CaptureIdentityAsync(CaptureIdentityParams request, RpcCallContext context)
    {
        RequireHello();
        if (!EnginePathRules.IsDeviceInterfacePath(request.DevicePath, EnginePathRules.DiskInterfaceGuid))
        {
            throw new BootrixException(ErrorCode.InvalidSpec, "device path is not a disk device path") { Arguments = ["device path is not a disk device path"] };
        }

        return await _engine.CaptureIdentityAsync(request.DevicePath, context.CancellationToken).ConfigureAwait(false);
    }

    private async Task<EngineJobResult> RunJobAsync(RunJobParams request, RpcCallContext context)
    {
        RequireHello();
        if (string.IsNullOrWhiteSpace(request.RunId) || request.RunId.Length > 64)
        {
            throw new BootrixException(ErrorCode.InvalidSpec, "run id is invalid") { Arguments = ["run id is invalid"] };
        }

        EngineRequestValidator.EnsureValid(request.Request);

        var started = Stopwatch.GetTimestamp();
        var pump = new ProgressPump(context.Connection, request.RunId);
        using var watchdog = new AbortWatchdog(_options.AbortGrace, _options.TimeProvider, OnAbortStuck, context.AbortToken);
        _logger.LogInformation("Job {RunId} ({Kind}) starts", request.RunId, request.Request.GetType().Name);

        EngineJobResult result;
        try
        {
            result = await _engine.RunJobAsync(request.Request, pump, context.CancellationToken, context.AbortToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = new EngineJobResult { Outcome = JobOutcome.Canceled, Duration = Stopwatch.GetElapsedTime(started) };
        }
        catch (Exception ex)
        {
            // The message may quote request data; the result carries it back to the caller, the log only gets the type.
            _logger.LogError("Job {RunId} failed with {ExceptionType}", request.RunId, ex.GetType().Name);
            result = EngineJobResult.From(new JobResult(JobOutcome.Failed, Stopwatch.GetElapsedTime(started), ex));
        }
        finally
        {
            await pump.CompleteAsync().ConfigureAwait(false);
        }

        _logger.LogInformation("Job {RunId} ended: {Outcome} {Code}", request.RunId, result.Outcome, result.ErrorCode);
        return result;
    }

    private void RequireHello()
    {
        if (!_helloDone)
        {
            throw new RpcFatalException("hello is required first");
        }
    }

    private void OnHelloTimeout()
    {
        if (_helloDone)
        {
            return;
        }

        _logger.LogWarning("No hello within {Timeout}, closing the connection", _options.HelloTimeout);
        _ = _connection.DisposeAsync().AsTask();
    }

    private void OnShutdown()
    {
        if (_helloDone)
        {
            _logger.LogInformation("Client asked the broker to shut down");
            _options.OnShutdownRequested?.Invoke();
        }
    }

    private void OnAbortStuck()
    {
        _logger.LogCritical("A job did not stop {Grace} after the abort", _options.AbortGrace);
        _options.OnAbortStuck?.Invoke();
    }

    private void OnDevicesChanged(object? sender, EventArgs e)
    {
        if (_helloDone)
        {
            _ = NotifyDevicesChangedAsync();
        }
    }

    private async Task NotifyDevicesChangedAsync()
    {
        try
        {
            await _connection.NotifyAsync(BrokerProtocol.DevicesChanged).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is RpcConnectionClosedException or RpcProtocolException)
        {
            // The client is gone.
        }
    }
}
