// SPDX-License-Identifier: GPL-3.0-or-later
using System.Management;
using Bootrix.Core.Workshop.Capture;

namespace Bootrix.Windows.Workshop;

/// <summary>
/// Reads the numerical recovery passwords of the BitLocker volumes (Win32_EncryptableVolume.GetKeyProtectorNumericalPassword).
/// Called only after the user opted in; the passwords are returned in the sensitive field and nowhere else.
/// </summary>
internal static class BitLockerRecoveryReader
{
    /// <summary>KeyProtectorType of a numerical password.</summary>
    private const uint NumericalPassword = 3;

    private const uint MethodSuccess = 0;

    public static (IReadOnlyList<BitLockerRecoveryKey> Keys, IReadOnlyList<string> VolumesWithoutKey) Read()
    {
        var keys = new List<BitLockerRecoveryKey>();
        var missing = new List<string>();

        foreach (var (volume, protectors) in Wmi.Select("SELECT * FROM Win32_EncryptableVolume", ReadVolume, BitLockerReader.Scope))
        {
            var found = false;
            foreach (var protector in protectors)
            {
                // A locked volume refuses the call; a password that does not pass the checksum is not a recovery password.
                if (RecoveryPassword.IsValid(protector.Password))
                {
                    keys.Add(new BitLockerRecoveryKey { Volume = volume, ProtectorId = protector.Id, RecoveryPassword = protector.Password });
                    found = true;
                }
            }

            if (!found && protectors.Count > 0)
            {
                missing.Add(volume);
            }
        }

        return (keys, missing);
    }

    private static (string Volume, List<(string Id, string? Password)> Protectors) ReadVolume(ManagementObject volume)
    {
        var name = Wmi.GetString(volume, "DriveLetter") ?? Wmi.GetString(volume, "DeviceID") ?? "?";
        var protectors = new List<(string, string?)>();

        foreach (var id in ProtectorIds(volume))
        {
            protectors.Add((id, ReadPassword(volume, id)));
        }

        return (name, protectors);
    }

    private static string[] ProtectorIds(ManagementObject volume)
    {
        try
        {
            using var input = volume.GetMethodParameters("GetKeyProtectors");
            input["KeyProtectorType"] = NumericalPassword;
            using var output = volume.InvokeMethod("GetKeyProtectors", input, null);
            return output is not null && Wmi.GetUInt(output, "ReturnValue") == MethodSuccess ? Wmi.GetStrings(output, "VolumeKeyProtectorID") : [];
        }
        catch (ManagementException)
        {
            return [];
        }
    }

    private static string? ReadPassword(ManagementObject volume, string protectorId)
    {
        try
        {
            using var input = volume.GetMethodParameters("GetKeyProtectorNumericalPassword");
            input["VolumeKeyProtectorID"] = protectorId;
            using var output = volume.InvokeMethod("GetKeyProtectorNumericalPassword", input, null);
            return output is not null && Wmi.GetUInt(output, "ReturnValue") == MethodSuccess ? Wmi.GetString(output, "NumericalPassword") : null;
        }
        catch (ManagementException)
        {
            return null;
        }
    }
}
