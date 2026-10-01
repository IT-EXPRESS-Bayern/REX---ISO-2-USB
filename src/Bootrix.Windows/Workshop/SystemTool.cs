// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Text;

namespace Bootrix.Windows.Workshop;

/// <summary>
/// Runs a console tool from System32 and returns what it printed. Only the system's own copy is started, never a program found
/// through PATH or the current directory, and the arguments go through ArgumentList so a profile name cannot inject switches.
/// </summary>
internal static class SystemTool
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    public static async Task<(int ExitCode, string Output)> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, fileName))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = ConsoleEncoding(),
            StandardErrorEncoding = ConsoleEncoding(),
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{fileName} did not start");
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout ?? DefaultTimeout);

        var output = process.StandardOutput.ReadToEndAsync(limit.Token);
        var error = process.StandardError.ReadToEndAsync(limit.Token);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            return (process.ExitCode, await output.ConfigureAwait(false) + await error.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException($"{fileName} did not finish in time");
        }
    }

    /// <summary>
    /// Console tools write in the OEM code page, not in UTF-8. That is the page of the system, which decides how the umlauts in
    /// translated labels and profile names come out.
    /// </summary>
    private static Encoding ConsoleEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            return Encoding.GetEncoding((int)FirmwareNative.GetOEMCP());
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already finished.
        }
    }
}
