// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using Bootrix.Core.Model;
using Bootrix.Core.Profiles;

namespace Bootrix.Core.Presentation;

/// <summary>
/// The command line that does what the window is set up to do, for scripts and for colleagues. Only what differs
/// from the defaults is written; passwords never are.
/// </summary>
public static class CliCommandBuilder
{
    public static string Write(JobSpec spec, string? imagePath, IEnumerable<string> disks)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(disks);

        var line = new StringBuilder("bootrix-cli write");
        if (!string.IsNullOrWhiteSpace(imagePath))
        {
            line.Append(' ').Append(Quote(imagePath));
        }

        foreach (var disk in disks)
        {
            line.Append(" -d ").Append(Quote(disk));
        }

        var target = spec.Target;
        var windows = spec.Windows;
        var defaults = new TargetOptions();

        Choice(line, "--mode", target.Mode, defaults.Mode);
        Choice(line, "--scheme", target.Scheme, defaults.Scheme);
        Choice(line, "--firmware", target.Firmware, defaults.Firmware);
        Choice(line, "--fs", target.FileSystem, defaults.FileSystem);
        Text(line, "--label", target.Label);
        Flag(line, "--legacy-bios", target.LegacyBiosFixes);
        if (target.PersistenceMegabytes > 0)
        {
            line.Append(" --persistence ").Append(target.PersistenceMegabytes.ToString(CultureInfo.InvariantCulture));
        }

        Flag(line, "--bypass-tpm", windows.BypassTpm);
        Flag(line, "--bypass-secure-boot", windows.BypassSecureBoot);
        Flag(line, "--bypass-ram", windows.BypassRam);
        Flag(line, "--bypass-cpu", windows.BypassCpu);
        Flag(line, "--bypass-storage", windows.BypassStorage);
        Text(line, "--local-account", windows.LocalAccountName);
        Flag(line, "--skip-privacy", windows.SkipPrivacyQuestions);
        Flag(line, "--no-device-encryption", windows.DisableBitLocker);
        Text(line, "--language", windows.UiLanguage);
        Text(line, "--time-zone", windows.TimeZone);
        Choice(line, "--boot-certificate", windows.BootCertificate, new WindowsSetupOptions().BootCertificate);
        if (windows.DriverFolders.Count > 0)
        {
            line.Append(" --drivers");
            foreach (var folder in windows.DriverFolders)
            {
                line.Append(' ').Append(Quote(folder));
            }
        }

        if (!spec.Verify.ReadBack)
        {
            line.Append(" --no-verify");
        }

        return line.ToString();
    }

    private static void Choice<T>(StringBuilder line, string option, T value, T defaultValue)
        where T : struct, Enum
    {
        if (!EqualityComparer<T>.Default.Equals(value, defaultValue))
        {
            line.Append(' ').Append(option).Append(' ').Append(value.ToString().ToLowerInvariant());
        }
    }

    private static void Text(StringBuilder line, string option, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            line.Append(' ').Append(option).Append(' ').Append(Quote(value));
        }
    }

    private static void Flag(StringBuilder line, string option, bool set)
    {
        if (set)
        {
            line.Append(' ').Append(option);
        }
    }

    private static string Quote(string value) =>
        value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':' or '\\' or '/')
            ? value
            : "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
