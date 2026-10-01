// SPDX-License-Identifier: GPL-3.0-or-later
using System.Management;
using System.Runtime.InteropServices;
using Bootrix.Core.Workshop;

namespace Bootrix.Windows.Workshop;

internal static class TpmReader
{
    private const string TpmScope = @"root\CIMV2\Security\MicrosoftTpm";

    /// <summary>
    /// Tbsi_GetDeviceInfo answers whether Windows sees a TPM and of which version, without any privilege. Whether it is enabled and
    /// activated comes from Win32_Tpm, which only an administrator may query; without it those two stay unknown.
    /// </summary>
    public static TpmInfo Read(IssueLog issues)
    {
        var (present, version) = ReadBaseServices(issues);
        var tpm = new TpmInfo { Present = present, SpecVersion = version };

        try
        {
            var instances = Wmi.Select(
                "SELECT SpecVersion, ManufacturerIdTxt, IsEnabled_InitialValue, IsActivated_InitialValue FROM Win32_Tpm",
                t => new TpmInfo
                {
                    Present = true,
                    // "2.0, 0, 1.38": specification version, revision, level.
                    SpecVersion = Wmi.GetString(t, "SpecVersion")?.Split(',')[0].Trim(),
                    Manufacturer = Wmi.GetString(t, "ManufacturerIdTxt"),
                    IsEnabled = Wmi.GetBool(t, "IsEnabled_InitialValue"),
                    IsActivated = Wmi.GetBool(t, "IsActivated_InitialValue"),
                },
                TpmScope);

            if (instances.Count > 0)
            {
                var wmi = instances[0];
                return tpm with
                {
                    Present = true,
                    SpecVersion = wmi.SpecVersion ?? tpm.SpecVersion,
                    Manufacturer = wmi.Manufacturer,
                    IsEnabled = wmi.IsEnabled,
                    IsActivated = wmi.IsActivated,
                };
            }

            // The class exists and has no instance: no TPM, unless TBS says otherwise.
            return tpm with { Present = tpm.Present ?? false };
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException)
        {
            issues.Add("tpm-details", ex);
            return tpm;
        }
    }

    private static (bool? Present, string? Version) ReadBaseServices(IssueLog issues)
    {
        try
        {
            var result = TbsNative.GetDeviceInfo((uint)Marshal.SizeOf<TbsNative.DeviceInfo>(), out var info);
            if (result == TbsNative.TpmNotFound)
            {
                return (false, null);
            }

            if (result != TbsNative.Success)
            {
                return (null, null);
            }

            var version = info.TpmVersion switch
            {
                TbsNative.TpmVersion12 => "1.2",
                TbsNative.TpmVersion20 => "2.0",
                _ => null,
            };
            return (true, version);
        }
        catch (DllNotFoundException ex)
        {
            issues.Add("tpm", ex);
            return (null, null);
        }
        catch (EntryPointNotFoundException ex)
        {
            issues.Add("tpm", ex);
            return (null, null);
        }
    }
}
