// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using Bootrix.Core.Tiny;
using Bootrix.Windows.Interop;
using Bootrix.Windows.Platform;
using Microsoft.Win32;
using CoreKind = Bootrix.Core.Tiny.RegistryValueKind;
using WinKind = Microsoft.Win32.RegistryValueKind;

namespace Bootrix.Windows.Dism;

/// <summary>A registry hive file of a mounted image, loaded below HKLM for the time it is edited.</summary>
public sealed class OfflineHive : IOfflineHive
{
    private const int UnloadAttempts = 20;

    private readonly string _mountName;
    private RegistryKey? _root;

    private OfflineHive(string mountName, RegistryKey root)
    {
        _mountName = mountName;
        _root = root;
    }

    public static OfflineHive Load(string hiveFile)
    {
        Privileges.Enable(Privileges.Backup, Privileges.Restore);

        var mountName = "BOOTRIX_" + Guid.NewGuid().ToString("N")[..12];
        var error = Advapi32.RegLoadKey(Advapi32.HkeyLocalMachine, mountName, hiveFile);
        if (error != Advapi32.ErrorSuccess)
        {
            throw new IOException($"Cannot load registry hive {hiveFile}", new Win32Exception(error));
        }

        var root = Registry.LocalMachine.OpenSubKey(mountName, writable: true);
        if (root is null)
        {
            Advapi32.RegUnLoadKey(Advapi32.HkeyLocalMachine, mountName);
            throw new IOException($"Hive {hiveFile} was loaded but cannot be opened");
        }

        return new OfflineHive(mountName, root);
    }

    public void SetValue(string key, string name, CoreKind kind, string value)
    {
        using var subKey = Root.CreateSubKey(key, writable: true);
        switch (kind)
        {
            case CoreKind.DWord:
                subKey.SetValue(name, unchecked((int)uint.Parse(value, System.Globalization.CultureInfo.InvariantCulture)), WinKind.DWord);
                break;
            default:
                subKey.SetValue(name, value, WinKind.String);
                break;
        }
    }

    public void DeleteKey(string key) => Root.DeleteSubKeyTree(key, throwOnMissingSubKey: false);

    public void DeleteValue(string key, string name)
    {
        using var subKey = Root.OpenSubKey(key, writable: true);
        subKey?.DeleteValue(name, throwOnMissingValue: false);
    }

    public void Dispose()
    {
        if (_root is null)
        {
            return;
        }

        _root.Dispose();
        _root = null;

        // Unloading fails while any handle into the hive is still open, including ones waiting for finalization.
        GC.Collect();
        GC.WaitForPendingFinalizers();

        var error = 0;
        for (var attempt = 0; attempt < UnloadAttempts; attempt++)
        {
            error = Advapi32.RegUnLoadKey(Advapi32.HkeyLocalMachine, _mountName);
            if (error == Advapi32.ErrorSuccess)
            {
                return;
            }

            Thread.Sleep(250);
            GC.Collect();
        }

        throw new IOException($"Cannot unload registry hive {_mountName}", new Win32Exception(error));
    }

    private RegistryKey Root => _root ?? throw new ObjectDisposedException(nameof(OfflineHive));
}
