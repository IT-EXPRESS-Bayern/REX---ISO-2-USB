// SPDX-License-Identifier: GPL-3.0-or-later
using System.Management;
using Bootrix.Core.Workshop;

namespace Bootrix.Windows.Workshop;

/// <summary>BitLocker state per volume through Win32_EncryptableVolume. The provider answers administrators only.</summary>
internal static class BitLockerReader
{
    public const string Scope = @"root\CIMV2\Security\MicrosoftVolumeEncryption";

    private const uint MethodSuccess = 0;

    private static readonly string[] EncryptionMethods =
    [
        "None",
        "Aes128Diffuser",
        "Aes256Diffuser",
        "Aes128",
        "Aes256",
        "HardwareEncryption",
        "XtsAes128",
        "XtsAes256",
    ];

    public static IReadOnlyList<BitLockerVolumeInfo> Read() =>
        Wmi.Select("SELECT * FROM Win32_EncryptableVolume", Describe, Scope);

    private static BitLockerVolumeInfo Describe(ManagementObject volume)
    {
        var volumeName = Wmi.GetString(volume, "DriveLetter") ?? Wmi.GetString(volume, "DeviceID") ?? "?";

        var protection = Wmi.GetUInt(volume, "ProtectionStatus") switch
        {
            0 => BitLockerProtection.Off,
            1 => BitLockerProtection.On,
            _ => BitLockerProtection.Unknown,
        };

        var conversion = Invoke(volume, "GetConversionStatus", "ConversionStatus") switch
        {
            0 => BitLockerConversion.FullyDecrypted,
            1 => BitLockerConversion.FullyEncrypted,
            2 => BitLockerConversion.EncryptionInProgress,
            3 => BitLockerConversion.DecryptionInProgress,
            4 => BitLockerConversion.EncryptionPaused,
            5 => BitLockerConversion.DecryptionPaused,
            _ => BitLockerConversion.Unknown,
        };

        var locked = Invoke(volume, "GetLockStatus", "LockStatus") switch
        {
            0 => (bool?)false,
            1 => true,
            _ => null,
        };

        var method = Invoke(volume, "GetEncryptionMethod", "EncryptionMethod");
        return new BitLockerVolumeInfo
        {
            Volume = volumeName,
            Protection = protection,
            Conversion = conversion,
            IsLocked = locked,
            EncryptionMethod = method is { } index && index < EncryptionMethods.Length ? EncryptionMethods[index] : null,
        };
    }

    /// <summary>Calls a method without arguments and returns one of its output values; null when the call failed, for example on a locked volume.</summary>
    private static uint? Invoke(ManagementObject volume, string method, string output)
    {
        try
        {
            using var result = volume.InvokeMethod(method, null, null);
            if (result is null || Wmi.GetUInt(result, "ReturnValue") != MethodSuccess)
            {
                return null;
            }

            return Wmi.GetUInt(result, output);
        }
        catch (ManagementException)
        {
            return null;
        }
    }
}
