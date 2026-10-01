// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Bootrix.Core;
using Bootrix.Core.Engine;
using Bootrix.Core.Hosting;
using Bootrix.Core.Images;
using Bootrix.Core.Ipc;
using Bootrix.Core.Jobs;
using Bootrix.Core.Logging;
using Bootrix.Core.Writing;
using Bootrix.Windows.Engine;
using Bootrix.Windows.Interop;
using Bootrix.Windows.Jobs;
using Bootrix.Windows.Platform;
using Bootrix.Windows.Storage;
using Bootrix.Windows.Writing;
using Microsoft.Extensions.Logging;

namespace Bootrix.Windows.Broker;

internal sealed record BrokerServeSettings
{
    public TimeSpan IdleTimeout { get; init; } = BrokerTimings.IdleTimeout;

    public TimeSpan AbortGrace { get; init; } = BrokerTimings.AbortGrace;

    public RpcConnectionOptions Connection { get; init; } = BrokerTimings.Connection;

    /// <summary>Called when a job still runs after an abort and the grace period.</summary>
    public Action? OnAbortStuck { get; init; }
}

/// <summary>
/// The elevated process. It builds the pipe, serves one client at a time with the local engine, and ends when the client
/// says it is done, when no client comes, or when the process that started it is gone. Whatever happens, the
/// engine runs inside this process only: the client sends requests and gets results, never rights.
/// </summary>
internal static class BrokerHost
{
    public static async Task<int> RunAsync(BrokerOptions options, BootrixPaths paths, CancellationToken cancellationToken)
    {
        using var loggers = LoggerFactory.Create(builder => builder
            .SetMinimumLevel(LogLevel.Information)
            .AddProvider(new FileLoggerProvider(paths.LogDirectory, filePrefix: "broker")));
        var logger = loggers.CreateLogger("Bootrix.Broker");

        try
        {
            logger.LogInformation("Broker {Version} starting for process {ParentProcessId}", AppInfo.Version, options.ParentProcessId);
            if (!ProcessElevation.IsElevated())
            {
                logger.LogError("The broker was not started elevated");
                return BrokerExitCodes.NotElevated;
            }

            byte[] secret;
            try
            {
                secret = BrokerSecretFile.ReadAndDelete(options.SecretFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                logger.LogError("The secret file could not be read: {ExceptionType}", ex.GetType().Name);
                return BrokerExitCodes.SecretUnavailable;
            }

            var ownImage = ProcessApi.GetImagePath(Environment.ProcessId)
                ?? throw new InvalidOperationException("The image path of the broker is unknown.");
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var parentExit = ParentProcessWatcher.WaitForExitAsync(options.ParentProcessId, ownImage, ProcessApi.GetImagePath, logger, stop.Token);
            var parentWatch = parentExit.ContinueWith(_ => stop.Cancel(), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);

            var verifier = new ClientProcessVerifier(ownImage, options.ParentProcessId, ProcessApi.GetImagePath);
            await using var listener = new SecurePipeListener(options.PipeName, options.UserSid, verifier, logger);
            using var disks = new DiskEnumerator(loggers.CreateLogger<DiskEnumerator>());
            var engine = CreateEngine(disks, listener, paths, loggers);

            await ServeAsync(
                listener,
                engine,
                secret,
                new BrokerServeSettings { OnAbortStuck = EndProcess },
                logger,
                stop.Token).ConfigureAwait(false);

            await stop.CancelAsync().ConfigureAwait(false);
            await parentWatch.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            logger.LogInformation("Broker ends");
            return BrokerExitCodes.Success;
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "The broker failed");
            return BrokerExitCodes.Failure;
        }
    }

    /// <summary>The server loop with the host wired to each client. Separate from the Windows setup so it can run on any system.</summary>
    internal static async Task<RpcServerStopReason> ServeAsync(
        IConnectionListener listener,
        IEngine engine,
        byte[] secret,
        BrokerServeSettings settings,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var hostOptions = new BrokerEngineHostOptions
        {
            Secret = secret,
            AbortGrace = settings.AbortGrace,
            OnAbortStuck = settings.OnAbortStuck,
            OnShutdownRequested = () => ThreadPool.QueueUserWorkItem(_ => stop.Cancel()),
            TimeProvider = settings.Connection.TimeProvider,
        };
        var server = new RpcServer(
            listener,
            (connection, _) => new BrokerEngineHost(engine, connection, hostOptions, logger),
            new RpcServerOptions { MaxClients = 1, IdleTimeout = settings.IdleTimeout, Connection = settings.Connection },
            logger);

        return await server.RunAsync(stop.Token).ConfigureAwait(false);
    }

    private static LocalEngine CreateEngine(DiskEnumerator disks, SecurePipeListener listener, BootrixPaths paths, ILoggerFactory loggers)
    {
        // Images are opened as the client, never with the broker's own rights; see ImpersonatingImageStreamProvider.
        var client = new PipeClientImpersonator(() => listener.CurrentPipe);
        var images = new ImpersonatingImageStreamProvider(new FileImageStreamProvider(), client);
        var journal = new JobJournal(paths.JournalDirectory);
        var rawWrite = new RawWriteJob(disks, images, journal, loggers.CreateLogger<RawWriteJob>());
        var services = new WriteServices(disks, new DiskPreparer(loggers.CreateLogger<DiskPreparer>()), journal, loggers, client);
        var writeImage = new WriteImageJobFactory(
            disks,
            new MediaPlanService(new ImageInspector()),
            images,
            MediaWriters.CreateDefault(services, images, rawWrite),
            paths,
            loggers.CreateLogger<WriteImageJobFactory>());
        return new LocalEngine(disks, new JobRunner(loggers.CreateLogger<JobRunner>()), rawWrite, writeImage, loggers.CreateLogger<LocalEngine>());
    }

    /// <summary>
    /// Last resort for a job that ignores the abort, typically stuck in a driver call on a failing device.
    /// Ending the process is the only thing that releases its handles; Windows closes them and the locks go with them.
    /// </summary>
    private static void EndProcess()
    {
        using var self = Process.GetCurrentProcess();
        self.Kill();
    }
}
