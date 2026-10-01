// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Workshop.Capture;

/// <summary>What to capture. The defaults collect no secret; each secret needs its own explicit opt-in.</summary>
public sealed record CustomerPcCaptureOptions
{
    public bool IncludeInstalledPrograms { get; init; } = true;

    public bool IncludeThirdPartyDrivers { get; init; } = true;

    /// <summary>List the names of the saved Wi-Fi profiles (netsh wlan show profiles). No key is read.</summary>
    public bool ListWlanProfiles { get; init; } = true;

    /// <summary>
    /// Opt-in: export all Wi-Fi profiles with their keys in clear text (netsh wlan export profile key=clear) into this folder.
    /// The caller chooses and protects the folder; Bootrix never picks one on its own.
    /// </summary>
    public string? WlanExportDirectory { get; init; }

    /// <summary>Opt-in: read the numerical recovery passwords of the BitLocker volumes.</summary>
    public bool IncludeBitLockerRecoveryKeys { get; init; }

    /// <summary>Opt-in: decode the Windows product key from the registry (DigitalProductId).</summary>
    public bool IncludeWindowsProductKey { get; init; }
}
