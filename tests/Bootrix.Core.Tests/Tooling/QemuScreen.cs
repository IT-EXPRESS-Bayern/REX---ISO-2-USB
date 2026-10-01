// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Bootrix.Core.Tests.Tooling;

/// <summary>
/// Boots an image in QEMU with SeaBIOS and reads the VGA text screen back through the monitor,
/// which is the only way to see what real-mode boot code actually prints.
/// </summary>
public static partial class QemuScreen
{
    public const string Tool = "qemu-system-x86_64";

    private const long TextBufferAddress = 0xB8000;
    private const int TextBufferBytes = 80 * 25 * 2;

    private static readonly string[] BaseArguments = ["-display", "none", "-monitor", "stdio", "-m", "32", "-no-reboot"];

    /// <summary>Polls the screen until it contains <paramref name="expected"/> or the timeout runs out; returns the last screen seen.</summary>
    public static string Boot(string diskArguments, string expected, TimeSpan timeout)
    {
        var path = ExternalTools.Find(Tool) ?? throw new FileNotFoundException(Tool);
        var info = new ProcessStartInfo(path)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in BaseArguments.Concat(diskArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
        {
            info.ArgumentList.Add(argument);
        }

        var output = new StringBuilder();
        using var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) =>
        {
            lock (output)
            {
                output.AppendLine(e.Data);
            }
        };
        process.Start();
        process.BeginOutputReadLine();
        _ = process.StandardError.ReadToEndAsync();

        var screen = "";
        var deadline = Stopwatch.StartNew();
        try
        {
            while (deadline.Elapsed < timeout)
            {
                Thread.Sleep(700);
                lock (output)
                {
                    output.Clear();
                }

                process.StandardInput.WriteLine($"xp /{TextBufferBytes}bx 0x{TextBufferAddress:X}");
                process.StandardInput.Flush();
                Thread.Sleep(300);

                string dump;
                lock (output)
                {
                    dump = output.ToString();
                }

                screen = Decode(dump);
                if (screen.Contains(expected, StringComparison.Ordinal))
                {
                    break;
                }
            }
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }

        return screen;
    }

    private static string Decode(string dump)
    {
        var cells = new List<byte>();
        foreach (var line in dump.Split('\n'))
        {
            var match = DumpLine().Match(line);
            if (!match.Success)
            {
                continue;
            }

            cells.AddRange(match.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(value => byte.Parse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
        }

        var rows = new List<string>();
        for (var row = 0; row * 160 < cells.Count; row++)
        {
            var text = new StringBuilder();
            for (var column = 0; column < 80 && (row * 160) + (column * 2) < cells.Count; column++)
            {
                var ch = cells[(row * 160) + (column * 2)];
                text.Append(ch is >= 0x20 and < 0x7F ? (char)ch : ' ');
            }

            rows.Add(text.ToString().TrimEnd());
        }

        return string.Join('\n', rows.Where(row => row.Length > 0));
    }

    [GeneratedRegex(@"^[0-9a-f]+:((?: 0x[0-9a-f]{2})+)\s*$")]
    private static partial Regex DumpLine();
}
