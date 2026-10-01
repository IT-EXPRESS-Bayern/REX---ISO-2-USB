// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Text;
using Bootrix.Core.Errors;

namespace Bootrix.Windows.Tools;

public sealed record ProcessResult(int ExitCode, string Output);

/// <summary>Runs a helper program and streams its output; the process is stopped when the caller cancels.</summary>
public static class ExternalProcess
{
    public static async Task<ProcessResult> RunAsync(
        string executable,
        string arguments,
        Action<string>? onOutput = null,
        CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo(executable, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = new Process { StartInfo = start };
        var output = new StringBuilder();
        var gate = new Lock();

        void Handle(string? text)
        {
            if (text is null)
            {
                return;
            }

            lock (gate)
            {
                output.AppendLine(text);
            }

            onOutput?.Invoke(text);
        }

        process.OutputDataReceived += (_, e) => Handle(e.Data);
        process.ErrorDataReceived += (_, e) => Handle(e.Data);

        if (!process.Start())
        {
            throw new BootrixException(ErrorCode.ExternalToolFailed, $"cannot start {executable}") { Arguments = [Path.GetFileName(executable), "start"] };
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await using var registration = cancellationToken.Register(() =>
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already finished.
            }
        });

        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        lock (gate)
        {
            return new ProcessResult(process.ExitCode, output.ToString());
        }
    }
}
