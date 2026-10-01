// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Hosting;
using Bootrix.Core.Logging;
using Microsoft.Extensions.Logging;

namespace Bootrix.Windows.Broker;

/// <summary>Exit codes of the broker process, for whoever looks at them in a log or a debugger.</summary>
internal static class BrokerExitCodes
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int BadArguments = 2;
    public const int NotElevated = 3;
    public const int SecretUnavailable = 4;
    public const int WorkspaceUntrusted = 5;
}

/// <summary>
/// The entry the program calls first. With --broker in the arguments the process becomes the elevated
/// broker and never shows a window; without it nothing happens and the normal start continues.
/// </summary>
public static class BrokerEntry
{
    /// <summary>Runs the broker if <paramref name="args"/> ask for it. Blocks until the broker ends and returns true, with the exit code to use.</summary>
    public static bool TryRun(string[] args, out int exitCode, BootrixPaths? paths = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (!BrokerOptions.IsBrokerRequest(args))
        {
            exitCode = 0;
            return false;
        }

        // Never the portable folder: users can write to it, and the broker keeps its log, journal and work files there.
        paths ??= BootrixPaths.ForInstalled();
        exitCode = RunAsync(args, paths).GetAwaiter().GetResult();
        return true;
    }

    private static async Task<int> RunAsync(string[] args, BootrixPaths paths)
    {
        if (BrokerOptions.TryParse(args, out var options, out var error))
        {
            return await BrokerHost.RunAsync(options!, paths, CancellationToken.None).ConfigureAwait(false);
        }

        using var provider = new FileLoggerProvider(paths.LogDirectory, filePrefix: "broker");
        provider.CreateLogger("Bootrix.Broker").LogError("The broker was started with invalid arguments: {Reason}", error);
        return BrokerExitCodes.BadArguments;
    }
}
