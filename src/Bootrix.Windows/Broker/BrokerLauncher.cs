// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using Bootrix.Core.Engine;
using Bootrix.Core.Errors;
using Bootrix.Windows.Interop;
using Bootrix.Windows.Platform;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Windows.Broker;

/// <summary>
/// Starts the elevated broker (the same program with --broker, through the shell with the "runas"
/// verb so that Windows shows the UAC prompt) and connects to it. The prompt appears here and only
/// here, so callers start the broker when something privileged is first needed, not at program start.
/// </summary>
public sealed class BrokerLauncher
{
    private const int ErrorCancelled = 1223;

    private readonly ILogger _logger;

    public BrokerLauncher(ILogger<BrokerLauncher>? logger = null) => _logger = logger ?? NullLogger<BrokerLauncher>.Instance;

    /// <exception cref="BootrixException">
    /// <see cref="ErrorCode.ElevationDenied"/> when the prompt was declined, <see cref="ErrorCode.BrokerStartFailed"/>
    /// when the broker did not come up, <see cref="ErrorCode.BrokerProtocol"/> when it does not speak our protocol.
    /// </exception>
    public async Task<BrokerEngineClient> LaunchAsync(CancellationToken cancellationToken)
    {
        var userSid = ProcessElevation.CurrentUserSid();
        var secret = RandomNumberGenerator.GetBytes(BrokerSecretFile.SecretBytes);
        using var secretFile = BrokerSecretFile.Create(secret, userSid);
        var options = new BrokerOptions(BrokerPipeName.Create(userSid), userSid, Environment.ProcessId, secretFile.FilePath);

        using var process = await StartElevatedAsync(options, cancellationToken).ConfigureAwait(false);
        var pipe = await ConnectAsync(options.PipeName, process, cancellationToken).ConfigureAwait(false);
        try
        {
            VerifyServer(pipe, process);
            return await BrokerEngineClient.ConnectAsync(
                pipe,
                new BrokerClientOptions { Secret = secret, Connection = BrokerTimings.Connection },
                _logger,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<Process?> StartElevatedAsync(BrokerOptions options, CancellationToken cancellationToken)
    {
        var info = LaunchCommand.Build(Environment.ProcessPath, EntryAssemblyPath(), options.ToCommandLine());
        _logger.LogInformation("Starting the elevated broker");

        // The call returns when the prompt is answered, which can take minutes, so it must not hold a UI thread.
        return await Task.Run(
            () =>
            {
                try
                {
                    return Process.Start(info);
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
                {
                    throw new BootrixException(ErrorCode.ElevationDenied, "the user declined the prompt", ex);
                }
                catch (Win32Exception ex)
                {
                    throw new BootrixException(ErrorCode.BrokerStartFailed, ex.Message, ex) { Arguments = [ex.Message] };
                }
            },
            CancellationToken.None).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<NamedPipeClientStream> ConnectAsync(string pipeName, Process? process, CancellationToken cancellationToken)
    {
        // Impersonation level "Impersonation" is what lets the broker open image files as this user.
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        using var giveUp = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        giveUp.CancelAfter(BrokerTimings.ConnectTimeout);

        var connect = pipe.ConnectAsync(Timeout.Infinite, giveUp.Token);
        var exited = WaitForExitAsync(process, giveUp.Token);
        try
        {
            if (await Task.WhenAny(connect, exited).ConfigureAwait(false) == connect)
            {
                await connect.ConfigureAwait(false);
                return pipe;
            }

            throw new BootrixException(ErrorCode.BrokerStartFailed, "the broker exited before it connected") { Arguments = ["the process ended"] };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw new BootrixException(ErrorCode.BrokerStartFailed, "timeout waiting for the pipe") { Arguments = ["timeout"] };
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            await giveUp.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(connect, exited).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    /// <summary>
    /// Completes when the broker process ended. The handle that the shell hands out for an elevated process
    /// may not allow waiting on it; then this simply never completes and the connect timeout is the safety net.
    /// </summary>
    private static async Task WaitForExitAsync(Process? process, CancellationToken cancellationToken)
    {
        if (process is not null)
        {
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                // Cannot observe the process.
            }
        }

        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Makes sure the pipe is served by the process that was started for it. The name is random and
    /// the ACL is ours, so this is a second line; when the shell does not reveal the process id there is nothing to compare.
    /// </summary>
    private void VerifyServer(NamedPipeClientStream pipe, Process? process)
    {
        int? expected = null;
        try
        {
            expected = process?.Id;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _logger.LogDebug("The process id of the broker is not available");
        }

        if (expected is null)
        {
            return;
        }

        if (!ProcessApi.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var actual) || actual != expected)
        {
            throw new BootrixException(ErrorCode.BrokerProtocol, "the pipe is served by another process") { Arguments = ["unexpected pipe server"] };
        }
    }

    private static string? EntryAssemblyPath() => System.Reflection.Assembly.GetEntryAssembly()?.Location;
}
