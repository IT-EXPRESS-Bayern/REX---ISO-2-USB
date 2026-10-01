// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;

namespace Bootrix.Core.Tests.Images.Support;

public sealed record ToolResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>Runs command line tools that serve as independent reference implementations in the tests.</summary>
public static class ExternalTool
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    public static bool Exists(string tool) => Find(tool) is not null;

    public static string? Find(string tool)
    {
        var extensions = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", "" } : [""];
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory, tool + extension);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>Runs the tool and fails the test when it exits with a non-zero code.</summary>
    public static ToolResult Run(string tool, params string[] arguments) => Run(tool, arguments, null, null, null);

    public static ToolResult Run(string tool, IEnumerable<string> arguments, string? workingDirectory, string? stdinFile, string? stdoutFile)
    {
        var result = RunUnchecked(tool, arguments, workingDirectory, stdinFile, stdoutFile);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{tool} exited with {result.ExitCode}: {result.StandardError}{result.StandardOutput}");
        }

        return result;
    }

    public static ToolResult RunUnchecked(string tool, IEnumerable<string> arguments, string? workingDirectory = null, string? stdinFile = null, string? stdoutFile = null)
    {
        var startInfo = new ProcessStartInfo(Find(tool) ?? tool)
        {
            RedirectStandardInput = stdinFile is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? string.Empty,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start " + tool);
        var error = process.StandardError.ReadToEndAsync();

        Task output;
        var text = new StringWriter();
        if (stdoutFile is not null)
        {
            var target = File.Create(stdoutFile);
            output = process.StandardOutput.BaseStream.CopyToAsync(target).ContinueWith(_ => target.Dispose(), TaskScheduler.Default);
        }
        else
        {
            output = process.StandardOutput.ReadToEndAsync().ContinueWith(t => text.Write(t.Result), TaskScheduler.Default);
        }

        if (stdinFile is not null)
        {
            using var input = File.OpenRead(stdinFile);
            input.CopyTo(process.StandardInput.BaseStream);
            process.StandardInput.Close();
        }

        if (!process.WaitForExit(Timeout))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{tool} did not finish within {Timeout.TotalSeconds:N0} s");
        }

        output.Wait();
        return new ToolResult(process.ExitCode, text.ToString(), error.Result);
    }
}
