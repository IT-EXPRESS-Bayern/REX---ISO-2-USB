// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;
using Bootrix.Core.Errors;
using Bootrix.Core.Tiny;

namespace Bootrix.Windows.Tools;

/// <summary>Creates a BIOS and UEFI bootable ISO from a Windows media folder with oscdimg.</summary>
public sealed partial class OscdimgIsoWriter(OscdimgLocator locator) : IIsoWriter
{
    public async Task CreateAsync(string mediaDirectory, string isoPath, string volumeLabel, bool uefi2023, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var oscdimg = await locator.GetPathAsync(cancellationToken).ConfigureAwait(false);
        var arguments = BuildArguments(mediaDirectory, isoPath, volumeLabel, uefi2023);

        var result = await ExternalProcess.RunAsync(
            oscdimg,
            arguments,
            line =>
            {
                if (TryParsePercent(line, out var percent))
                {
                    progress?.Report(percent / 100.0);
                }
            },
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new BootrixException(ErrorCode.ExternalToolFailed, $"oscdimg exit code {result.ExitCode}\n{result.Output}")
            {
                Arguments = ["oscdimg", result.ExitCode],
            };
        }
    }

    internal static string BuildArguments(string mediaDirectory, string isoPath, string volumeLabel, bool uefi2023)
    {
        var bios = Path.Combine(mediaDirectory, "boot", "etfsboot.com");
        var efi = FindEfiBootImage(mediaDirectory, uefi2023);
        if (!File.Exists(bios))
        {
            throw new BootrixException(ErrorCode.ImageUnsupported, "boot\\etfsboot.com is missing") { Arguments = ["boot\\etfsboot.com"] };
        }

        // Two boot entries: the BIOS loader (platform 0) and the UEFI image (platform 0xEF).
        var bootData = $"-bootdata:2#p0,e,b\"{bios}\"#pEF,e,b\"{efi}\"";
        return $"-m -o -u2 -udfver102 -l{SanitizeLabel(volumeLabel)} {bootData} \"{mediaDirectory}\" \"{isoPath}\"";
    }

    internal static string FindEfiBootImage(string mediaDirectory, bool uefi2023)
    {
        var folder = Path.Combine(mediaDirectory, "efi", "microsoft", "boot");
        var names = uefi2023
            ? new[] { "efisys_EX.bin", "efisys_noprompt_EX.bin" }
            : new[] { "efisys_noprompt.bin", "efisys.bin" };

        foreach (var name in names)
        {
            var path = Path.Combine(folder, name);
            if (File.Exists(path))
            {
                return path;
            }
        }

        throw new BootrixException(ErrorCode.ImageUnsupported, $"no EFI boot image in {folder}") { Arguments = ["efi\\microsoft\\boot\\efisys.bin"] };
    }

    /// <summary>ISO 9660 / UDF volume labels are limited to 32 characters; keep it to the safe subset.</summary>
    internal static string SanitizeLabel(string label)
    {
        var clean = new string(label.ToUpperInvariant().Select(c => char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
        return clean.Length == 0 ? "BOOTRIX" : clean[..Math.Min(clean.Length, 32)];
    }

    internal static bool TryParsePercent(string line, out double percent)
    {
        var match = PercentPattern().Match(line);
        if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out percent))
        {
            return true;
        }

        percent = 0;
        return false;
    }

    [GeneratedRegex(@"(\d{1,3})%\s+complete", RegexOptions.IgnoreCase)]
    private static partial Regex PercentPattern();
}
