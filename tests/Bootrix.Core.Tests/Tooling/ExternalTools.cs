// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Text;

namespace Bootrix.Core.Tests.Tooling;

public sealed record ToolResult(int ExitCode, string Output, string Error)
{
    public string Combined => Output + Error;
}

/// <summary>
/// Runs the command-line tools the format tests are checked against. Tests that need a tool
/// skip themselves (see <see cref="RequiresToolFactAttribute"/>) instead of failing on machines
/// without it.
/// </summary>
public static class ExternalTools
{
    private static readonly string[] ExtraDirectories = ["/usr/sbin", "/sbin", "/usr/local/sbin"];

    public static string? Find(string name)
    {
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(ExtraDirectories);

        foreach (var directory in directories)
        {
            foreach (var candidate in new[] { name, name + ".exe" })
            {
                var path = Path.Combine(directory, candidate);
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    public static bool IsAvailable(string name) => Find(name) is not null;

    public static ToolResult Run(string tool, params string[] arguments)
    {
        var path = Find(tool) ?? throw new FileNotFoundException($"Tool '{tool}' is not installed.");
        var info = new ProcessStartInfo(path)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment["LC_ALL"] = "C";
        info.Environment["MTOOLS_SKIP_CHECK"] = "1";

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {tool}.");
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return new ToolResult(process.ExitCode, output, error.GetAwaiter().GetResult());
    }
}

public sealed class RequiresToolFactAttribute : FactAttribute
{
    public RequiresToolFactAttribute(params string[] tools)
    {
        var missing = tools.Where(tool => !ExternalTools.IsAvailable(tool)).ToArray();
        if (missing.Length > 0)
        {
            Skip = $"Required tool not installed: {string.Join(", ", missing)}";
        }
    }
}

public sealed class RequiresToolTheoryAttribute : TheoryAttribute
{
    public RequiresToolTheoryAttribute(params string[] tools)
    {
        var missing = tools.Where(tool => !ExternalTools.IsAvailable(tool)).ToArray();
        if (missing.Length > 0)
        {
            Skip = $"Required tool not installed: {string.Join(", ", missing)}";
        }
    }
}
