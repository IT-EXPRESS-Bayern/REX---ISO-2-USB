// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bootrix.Core.Errors;
using Bootrix.Core.Json;
using Bootrix.Core.Jobs;

namespace Bootrix.Cli.Output;

/// <summary>Human readable output by default, one JSON object per line with --json.</summary>
public sealed class ConsoleWriter(bool json, TextWriter? output = null, TextWriter? error = null)
{
    private readonly TextWriter _out = output ?? Console.Out;
    private readonly TextWriter _err = error ?? Console.Error;
    private int _lastProgressLength;

    public bool Json { get; } = json;

    public void WriteObject(object value)
    {
        _out.WriteLine(JsonSerializer.Serialize(value, CoreJson.Options.WithoutIndent()));
    }

    public void WriteLine(string text = "") => _out.WriteLine(text);

    public void WriteTable(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var widths = headers.Select((h, i) => Math.Max(h.Length, rows.Count == 0 ? 0 : rows.Max(r => r[i].Length))).ToArray();
        _out.WriteLine(string.Join("  ", headers.Select((h, i) => h.PadRight(widths[i]))));
        _out.WriteLine(string.Join("  ", widths.Select(w => new string('-', w))));
        foreach (var row in rows)
        {
            _out.WriteLine(string.Join("  ", row.Select((c, i) => c.PadRight(widths[i]))));
        }
    }

    public void WriteError(Exception exception)
    {
        var description = ErrorCatalog.Describe(exception);
        if (Json)
        {
            WriteObject(new { type = "error", code = description.Code, message = description.Cause, action = description.Action, detail = exception.Message });
            return;
        }

        _err.WriteLine($"{description.Code}: {description.Cause}");
        _err.WriteLine($"  {description.Action}");
    }

    public void WriteProgress(ProgressReport report)
    {
        if (Json)
        {
            WriteObject(new
            {
                type = "progress",
                step = report.StepKey,
                stepIndex = report.StepIndex,
                stepCount = report.StepCount,
                overall = Math.Round(report.OverallFraction, 4),
                bytesDone = report.BytesDone,
                bytesTotal = report.BytesTotal,
                bytesPerSecond = (long)report.BytesPerSecond,
                etaSeconds = report.Eta?.TotalSeconds,
                detail = report.Detail,
            });
            return;
        }

        var line = FormatProgress(report);
        _err.Write('\r' + line.PadRight(_lastProgressLength));
        _lastProgressLength = line.Length;
    }

    public void EndProgress()
    {
        if (!Json && _lastProgressLength > 0)
        {
            _err.WriteLine();
            _lastProgressLength = 0;
        }
    }

    internal static string FormatProgress(ProgressReport report)
    {
        var text = $"[{report.StepIndex + 1}/{report.StepCount}] {report.StepKey,-22} {report.OverallFraction * 100,5:0.0}%";
        if (report.BytesPerSecond > 1024)
        {
            text += $"  {FormatSize((long)report.BytesPerSecond)}/s";
        }

        if (report.Eta is { } eta)
        {
            text += $"  ETA {(eta.TotalHours >= 1 ? $"{(int)eta.TotalHours}:{eta.Minutes:00}:{eta.Seconds:00}" : $"{eta.Minutes}:{eta.Seconds:00}")}";
        }

        return text;
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 40 => $"{bytes / (double)(1L << 40):0.##} TB",
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };
}

internal static class JsonOptionsExtensions
{
    private static readonly JsonSerializerOptions Compact = new(CoreJson.Options) { WriteIndented = false };

    public static JsonSerializerOptions WithoutIndent(this JsonSerializerOptions options) => options.WriteIndented ? Compact : options;
}
