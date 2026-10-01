// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Workshop.Capture;
using Microsoft.Win32;

namespace Bootrix.Windows.Workshop;

internal static class InstalledProgramReader
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>
    /// 64-bit and 32-bit programs register in separate views of HKLM, and programs installed for one user only appear below HKCU.
    /// Other users' hives are not loaded and stay out.
    /// </summary>
    public static IReadOnlyList<InstalledProgram> Read()
    {
        var entries = new List<UninstallEntry>();
        foreach (var (hive, view) in new[] { (RegistryHive.LocalMachine, RegistryView.Registry64), (RegistryHive.LocalMachine, RegistryView.Registry32), (RegistryHive.CurrentUser, RegistryView.Default) })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(UninstallKey);
            if (uninstall is null)
            {
                continue;
            }

            foreach (var name in uninstall.GetSubKeyNames())
            {
                using var program = uninstall.OpenSubKey(name);
                if (program is not null)
                {
                    entries.Add(ReadEntry(program));
                }
            }
        }

        return InstalledProgramFilter.Normalize(entries);
    }

    private static UninstallEntry ReadEntry(RegistryKey key) => new(
        key.GetValue("DisplayName") as string,
        key.GetValue("DisplayVersion") as string,
        key.GetValue("Publisher") as string,
        key.GetValue("SystemComponent") is 1,
        key.GetValue("ParentKeyName") as string,
        key.GetValue("ReleaseType") as string);
}
