// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Bootrix.Core.Tests.Tooling;

namespace Bootrix.Core.Tests.Writing.Windows.BootCode;

/// <summary>Boots a disk image with OVMF in QEMU and watches the serial port, where both OVMF's console and the test application write.</summary>
internal static class QemuUefi
{
    private static readonly string[] Firmware =
    [
        "/usr/share/ovmf/OVMF.fd",
        "/usr/share/OVMF/OVMF.fd",
        "/usr/share/edk2/ovmf/OVMF.fd",
        "/usr/share/qemu/OVMF.fd",
    ];

    public static string? FirmwarePath => Firmware.FirstOrDefault(File.Exists);

    public static bool IsAvailable => ExternalTools.IsAvailable(QemuScreen.Tool) && FirmwarePath is not null;

    /// <summary>Returns everything the serial port received until <paramref name="expected"/> showed up or the time ran out.</summary>
    public static string Boot(string diskArguments, string expected, TimeSpan timeout)
    {
        var serialFile = Path.Combine(Path.GetTempPath(), "bootrix-serial-" + Guid.NewGuid().ToString("N") + ".txt");
        var info = new ProcessStartInfo(ExternalTools.Find(QemuScreen.Tool)!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in new[] { "-machine", "q35", "-m", "256", "-display", "none", "-no-reboot", "-serial", "file:" + serialFile, "-bios", FirmwarePath! }
            .Concat(diskArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries)))
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info)!;
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();

        var text = "";
        var clock = Stopwatch.StartNew();
        try
        {
            while (clock.Elapsed < timeout && !process.HasExited)
            {
                Thread.Sleep(500);
                text = ReadShared(serialFile);
                if (text.Contains(expected, StringComparison.Ordinal))
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
                process.WaitForExit();
            }

            text = ReadShared(serialFile);
            File.Delete(serialFile);
        }

        return text;
    }

    private static string ReadShared(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return "";
        }
    }
}

/// <summary>A fact that needs QEMU and an OVMF firmware image.</summary>
public sealed class RequiresQemuUefiFactAttribute : FactAttribute
{
    public RequiresQemuUefiFactAttribute(params string[] moreTools)
    {
        if (!QemuUefi.IsAvailable)
        {
            Skip = "QEMU or the OVMF firmware is not installed";
            return;
        }

        var missing = moreTools.Where(tool => !ExternalTools.IsAvailable(tool)).ToArray();
        if (missing.Length > 0)
        {
            Skip = "Required tool not installed: " + string.Join(", ", missing);
        }
    }
}
