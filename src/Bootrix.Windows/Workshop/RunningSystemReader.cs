// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Runtime.InteropServices;
using Bootrix.Core.Workshop;
using Microsoft.Win32;

namespace Bootrix.Windows.Workshop;

internal static class RunningSystemReader
{
    private const string VersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private const string MiniNtKey = @"SYSTEM\CurrentControlSet\Control\MiniNT";

    public static RunningSystemInfo Read()
    {
        string? product = null;
        string? edition = null;
        string? displayVersion = null;
        string? installationType = null;
        using (var key = Registry.LocalMachine.OpenSubKey(VersionKey))
        {
            product = key?.GetValue("ProductName") as string;
            edition = key?.GetValue("EditionID") as string;
            displayVersion = key?.GetValue("DisplayVersion") as string ?? key?.GetValue("ReleaseId") as string;
            installationType = key?.GetValue("InstallationType") as string;
        }

        using var miniNt = Registry.LocalMachine.OpenSubKey(MiniNtKey);

        return new RunningSystemInfo
        {
            ProductName = product,
            EditionId = edition,
            // The build from the OS itself: the registry still reports "Windows 10" for Windows 11.
            BuildNumber = Environment.OSVersion.Version.Build,
            DisplayVersion = displayVersion,
            Architecture = CpuReader.ArchitectureOf(RuntimeInformation.OSArchitecture),
            IsServer = installationType?.StartsWith("Server", StringComparison.OrdinalIgnoreCase),
            IsWinPe = miniNt is not null,
            UiLanguage = CultureInfo.InstalledUICulture.Name,
            TimeZoneId = TimeZoneInfo.Local.Id,
            KeyboardLayouts = ReadKeyboardLayouts(),
        };
    }

    /// <summary>
    /// "0407:00000407" is the form of InputLocale in unattend files: the language ID, a colon, then the keyboard layout ID. A layout ID
    /// ends in the language ID it belongs to.
    /// </summary>
    internal static string InputLocale(string keyboardLayoutId) =>
        keyboardLayoutId.Length >= 4 ? $"{keyboardLayoutId[^4..]}:{keyboardLayoutId}" : keyboardLayoutId;

    private static List<string> ReadKeyboardLayouts()
    {
        using var preload = Registry.CurrentUser.OpenSubKey(@"Keyboard Layout\Preload");
        if (preload is null)
        {
            return [];
        }

        // A preloaded ID such as d0010409 may be substituted by the layout that is really used (00010409).
        using var substitutes = Registry.CurrentUser.OpenSubKey(@"Keyboard Layout\Substitutes");
        var layouts = new List<string>();
        foreach (var name in preload.GetValueNames().OrderBy(n => int.TryParse(n, CultureInfo.InvariantCulture, out var order) ? order : int.MaxValue))
        {
            if (preload.GetValue(name) is not string id || id.Length == 0)
            {
                continue;
            }

            var layout = substitutes?.GetValue(id) as string ?? id;
            var locale = InputLocale(layout.ToUpperInvariant());
            if (!layouts.Contains(locale, StringComparer.Ordinal))
            {
                layouts.Add(locale);
            }
        }

        return layouts;
    }
}
