// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Bootrix.Core.Workshop;
using Bootrix.Core.Workshop.Firmware;
using Bootrix.Windows.Platform;
using Microsoft.Win32;

namespace Bootrix.Windows.Workshop;

/// <summary>Firmware type, Secure Boot state and which boot manager certificates the firmware trusts.</summary>
internal static unsafe class FirmwareReader
{
    private const string SecureBootStateKey = @"SYSTEM\CurrentControlSet\Control\SecureBoot\State";

    private const int InitialVariableSize = 64 * 1024;
    private const int MaximumVariableSize = 4 * 1024 * 1024;

    public static FirmwareType ReadType()
    {
        if (!FirmwareNative.GetFirmwareType(out var type))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }

        return type switch
        {
            FirmwareNative.FirmwareTypeBios => FirmwareType.Bios,
            FirmwareNative.FirmwareTypeUefi => FirmwareType.Uefi,
            _ => FirmwareType.Unknown,
        };
    }

    /// <summary>
    /// The registry value needs no privilege, so it comes first; the firmware variable needs administrator rights and
    /// SeSystemEnvironmentPrivilege. Legacy BIOS has no Secure Boot state at all, which is reported as unknown.
    /// </summary>
    public static SecureBootState ReadSecureBoot(FirmwareType type)
    {
        if (type != FirmwareType.Uefi)
        {
            return SecureBootState.Unknown;
        }

        using (var key = Registry.LocalMachine.OpenSubKey(SecureBootStateKey))
        {
            if (key?.GetValue("UEFISecureBootEnabled") is int enabled)
            {
                return enabled != 0 ? SecureBootState.On : SecureBootState.Off;
            }
        }

        var value = ReadVariable("SecureBoot", SecureBootDatabase.GlobalGuid);
        return value is { Length: > 0 } ? (value[0] != 0 ? SecureBootState.On : SecureBootState.Off) : SecureBootState.Unknown;
    }

    /// <summary>Returns null when db and dbx cannot be read, for example without administrator rights.</summary>
    public static SecureBootCertificateInfo? ReadCertificates()
    {
        var db = ReadVariable("db", SecureBootDatabase.DatabaseGuid);
        var dbx = ReadVariable("dbx", SecureBootDatabase.DatabaseGuid);
        if (db is null && dbx is null)
        {
            return null;
        }

        return new SecureBootCertificateInfo
        {
            Windows2023InDb = db is null ? null : SecureBootDatabase.ContainsCertificate(db, SecureBootDatabase.WindowsUefiCa2023),
            Windows2011InDb = db is null ? null : SecureBootDatabase.ContainsCertificate(db, SecureBootDatabase.WindowsProductionPca2011),
            Windows2011RevokedInDbx = dbx is null ? null : SecureBootDatabase.ContainsCertificate(dbx, SecureBootDatabase.WindowsProductionPca2011),
        };
    }

    /// <summary>Reads a UEFI variable; null when it does not exist or the process lacks the privilege.</summary>
    private static byte[]? ReadVariable(string name, string guid)
    {
        try
        {
            Privileges.Enable(Privileges.SystemEnvironment);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }

        for (var size = InitialVariableSize; size <= MaximumVariableSize; size *= 2)
        {
            var buffer = new byte[size];
            uint read;
            fixed (byte* pointer = buffer)
            {
                read = FirmwareNative.GetFirmwareEnvironmentVariable(name, guid, pointer, (uint)size);
            }

            if (read > 0)
            {
                return buffer[..(int)read];
            }

            // Only a too small buffer is worth a retry; a missing variable or legacy firmware ends here.
            if (Marshal.GetLastPInvokeError() != FirmwareNative.ErrorInsufficientBuffer)
            {
                return null;
            }
        }

        return null;
    }
}
