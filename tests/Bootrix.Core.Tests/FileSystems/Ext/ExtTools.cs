// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Text;

namespace Bootrix.Core.Tests.FileSystems.Ext;

public sealed record ToolResult(int ExitCode, string Output, string Error)
{
    /// <summary>Standard output and error together, for assertion messages.</summary>
    public string All => Output + Error;
}

/// <summary>Locates and runs the e2fsprogs binaries used as independent references.</summary>
internal static class ExtTools
{
    private static readonly string[] BaselineTools = ["e2fsck", "dumpe2fs", "debugfs"];

    private static readonly string[] SearchDirectories = ["/sbin", "/usr/sbin", "/usr/local/sbin", "/bin", "/usr/bin"];

    public static string? Find(string tool)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator).Concat(SearchDirectories))
        {
            if (directory.Length == 0)
            {
                continue;
            }

            var candidate = Path.Combine(directory, tool);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Null when e2fsck, dumpe2fs, debugfs and the additional tools are all installed, otherwise the reason to skip.</summary>
    public static string? SkipReason(params string[] additionalTools)
    {
        var missing = BaselineTools.Concat(additionalTools).Where(tool => Find(tool) is null).ToArray();
        return missing.Length == 0 ? null : $"Required tools are not installed: {string.Join(", ", missing)}.";
    }

    public static ToolResult Run(string tool, params string[] arguments)
    {
        var path = Find(tool) ?? throw new FileNotFoundException($"{tool} is not installed.");
        var info = new ProcessStartInfo(path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.Environment["LC_ALL"] = "C";
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {tool}.");
        var output = new StringBuilder();
        var error = new StringBuilder();
        process.OutputDataReceived += (_, e) => Append(output, e.Data);
        process.ErrorDataReceived += (_, e) => Append(error, e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{tool} did not finish within five minutes.");
        }

        // The parameterless overload waits until the asynchronous output handlers have drained.
        process.WaitForExit();
        return new ToolResult(process.ExitCode, output.ToString(), error.ToString());
    }

    /// <summary>Runs e2fsck in read-only forced mode; exit code 0 means the file system is clean.</summary>
    public static ToolResult Fsck(string image) => Run("e2fsck", "-fn", image);

    public static string Debugfs(string image, string request) => Run("debugfs", "-R", request, image).Output;

    private static void Append(StringBuilder builder, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (builder)
        {
            builder.Append(line).Append('\n');
        }
    }
}
