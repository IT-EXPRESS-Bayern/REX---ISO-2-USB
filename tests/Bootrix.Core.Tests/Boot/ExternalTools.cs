// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;

namespace Bootrix.Core.Tests.Boot;

internal sealed record ToolResult(int ExitCode, string StdOut, string StdErr)
{
    public string Output => StdOut + StdErr;
}

internal static class ExternalTools
{
    public static bool IsAvailable(string tool) => Find(tool) is not null;

    public static string? Find(string tool)
    {
        var names = OperatingSystem.IsWindows() ? new[] { tool + ".exe", tool } : [tool];
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    public static ToolResult Run(string tool, IEnumerable<string> arguments, string? workingDirectory = null, int timeoutMilliseconds = 120_000)
    {
        var info = new ProcessStartInfo(Find(tool) ?? throw new InvalidOperationException(tool + " is not installed"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMilliseconds))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(tool + " did not finish in time");
        }

        return new ToolResult(process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    public static ToolResult RunChecked(string tool, params string[] arguments)
    {
        var result = Run(tool, arguments);
        return result.ExitCode == 0
            ? result
            : throw new InvalidOperationException($"{tool} failed with {result.ExitCode}: {result.Output}");
    }
}

/// <summary>A fact that is skipped, not failed, when one of the named command-line tools is missing.</summary>
public sealed class ToolFactAttribute : FactAttribute
{
    public ToolFactAttribute(params string[] tools)
    {
        var missing = tools.Where(t => !ExternalTools.IsAvailable(t)).ToArray();
        if (missing.Length > 0)
        {
            Skip = "requires " + string.Join(", ", missing);
        }
    }
}

public sealed class ToolTheoryAttribute : TheoryAttribute
{
    public ToolTheoryAttribute(params string[] tools)
    {
        var missing = tools.Where(t => !ExternalTools.IsAvailable(t)).ToArray();
        if (missing.Length > 0)
        {
            Skip = "requires " + string.Join(", ", missing);
        }
    }
}

/// <summary>
/// A fact that needs real signed EFI files. Point BOOTRIX_EFI_SAMPLES at a directory that holds them
/// (see <see cref="RealSamples"/>); without it the test is skipped.
/// </summary>
public sealed class RealSampleFactAttribute : FactAttribute
{
    public RealSampleFactAttribute()
    {
        if (RealSamples.Directory is null)
        {
            Skip = "set BOOTRIX_EFI_SAMPLES to a directory with real EFI files";
        }
    }
}

internal static class RealSamples
{
    public static string? Directory { get; } = FindDirectory();

    public static string Path(string relative) => System.IO.Path.Combine(Directory!, relative);

    public static bool Exists(string relative) => Directory is not null && File.Exists(Path(relative));

    private static string? FindDirectory()
    {
        var value = Environment.GetEnvironmentVariable("BOOTRIX_EFI_SAMPLES");
        return !string.IsNullOrEmpty(value) && System.IO.Directory.Exists(value) ? value : null;
    }
}
