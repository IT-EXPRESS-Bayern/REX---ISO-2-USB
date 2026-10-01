// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;

namespace Bootrix.Core.Tests.Images;

/// <summary>Runs command-line tools that are used as independent references in the image tests.</summary>
internal static class ExternalTool
{
    /// <summary>Finds the first of the names on PATH; a single argument may list alternatives separated by '|'.</summary>
    public static string? Find(params string[] names)
    {
        names = [.. names.SelectMany(n => n.Split('|'))];
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var name in names)
        {
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
        }

        return null;
    }

    public static bool Exists(params string[] names) => Find(names) is not null;

    public static (int ExitCode, string Output) Run(string tool, IEnumerable<string> arguments, TimeSpan? timeout = null)
    {
        var info = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("cannot start " + tool);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout ?? TimeSpan.FromMinutes(2)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(tool + " did not finish in time");
        }

        process.WaitForExit();
        return (process.ExitCode, output.Result + error.Result);
    }
}

/// <summary>A fact that is reported as skipped when one of the named tools is not installed.</summary>
internal sealed class RequiresToolFactAttribute : FactAttribute
{
    public RequiresToolFactAttribute(params string[] tools)
    {
        var missing = tools.Where(t => !ExternalTool.Exists(t)).ToArray();
        if (missing.Length > 0)
        {
            Skip = "requires " + string.Join(", ", missing);
        }
    }
}
