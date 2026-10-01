// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Profiles;
using Bootrix.Core.Tiny;

namespace Bootrix.Core.Writing.Windows.Customization;

/// <summary>
/// The values under HKLM\SYSTEM\Setup\LabConfig that make Windows 11 Setup accept hardware it would otherwise
/// refuse. The same values are set from the answer file (windowsPE pass) and in the Setup image itself, see
/// <see cref="BootImagePatcher"/>; they are the ones the Tiny profiles carry in <see cref="TinyProfile.BootWimRegistry"/>.
/// </summary>
public static class HardwareCheckBypass
{
    /// <summary>Key below the SYSTEM hive of the Setup image.</summary>
    public const string LabConfigKey = @"Setup\LabConfig";

    public static bool IsRequested(WindowsSetupOptions windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        return windows.BypassTpm || windows.BypassSecureBoot || windows.BypassRam || windows.BypassCpu || windows.BypassStorage;
    }

    /// <summary>One registry change per requested check, in a fixed order.</summary>
    public static IReadOnlyList<RegistryChange> Changes(WindowsSetupOptions windows)
    {
        ArgumentNullException.ThrowIfNull(windows);

        var names = new List<string>();
        if (windows.BypassTpm)
        {
            names.Add("BypassTPMCheck");
        }

        if (windows.BypassSecureBoot)
        {
            names.Add("BypassSecureBootCheck");
        }

        if (windows.BypassRam)
        {
            names.Add("BypassRAMCheck");
        }

        if (windows.BypassCpu)
        {
            names.Add("BypassCPUCheck");
        }

        if (windows.BypassStorage)
        {
            names.Add("BypassStorageCheck");
        }

        return
        [
            .. names.Select(name => new RegistryChange
            {
                Hive = RegistryHive.System,
                Key = LabConfigKey,
                Name = name,
                Kind = RegistryValueKind.DWord,
                Value = "1",
            }),
        ];
    }
}
