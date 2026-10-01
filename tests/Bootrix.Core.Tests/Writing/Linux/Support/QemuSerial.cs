// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Text;
using Bootrix.Core.Tests.Tooling;

namespace Bootrix.Core.Tests.Writing.Linux.Support;

/// <summary>Boots a disk image in QEMU and reads what the guest writes to its first serial port.</summary>
internal static class QemuSerial
{
    public const string Tool = QemuScreen.Tool;

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private static readonly string[] BaseArguments = ["-m", "128", "-display", "none", "-serial", "stdio", "-monitor", "none", "-no-reboot"];

    /// <summary>
    /// Runs the machine until the output contains <paramref name="expected"/> or the time is up, then stops it.
    /// Returns everything received, with control characters removed so that terminal escape codes of the boot loaders do not get in the way.
    /// </summary>
    public static string Boot(IEnumerable<string> driveArguments, string expected, TimeSpan? timeout = null)
    {
        var path = ExternalTools.Find(Tool) ?? throw new FileNotFoundException(Tool);
        var info = new ProcessStartInfo(path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        foreach (var argument in BaseArguments.Concat(driveArguments))
        {
            info.ArgumentList.Add(argument);
        }

        var output = new StringBuilder();
        var found = new ManualResetEventSlim();
        using var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (output)
            {
                output.AppendLine(Clean(e.Data));
                if (output.ToString().Contains(expected, StringComparison.Ordinal))
                {
                    found.Set();
                }
            }
        };
        process.Start();
        process.BeginOutputReadLine();
        _ = process.StandardError.ReadToEndAsync();
        process.StandardInput.Close();

        try
        {
            found.Wait(timeout ?? DefaultTimeout);
            if (!found.IsSet)
            {
                // The serial line may end without a newline; give the last partial chunk a moment to arrive.
                Thread.Sleep(500);
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }

        lock (output)
        {
            return output.ToString();
        }
    }

    public static IEnumerable<string> HardDisk(string imagePath) =>
        ["-drive", $"file={imagePath},format=raw,if=ide,index=0"];

    private static string Clean(string line)
    {
        var builder = new StringBuilder(line.Length);
        var escape = false;
        foreach (var ch in line)
        {
            if (escape)
            {
                // CSI sequences end with a letter; the rest of the sequence is dropped with it.
                escape = !char.IsLetter(ch);
                continue;
            }

            if (ch == '\u001b')
            {
                escape = true;
                continue;
            }

            if (ch is '\r' or '\n' || ch >= ' ')
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }
}
