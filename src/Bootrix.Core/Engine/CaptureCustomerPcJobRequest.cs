// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Engine;

/// <summary>
/// Takes the inventory of this PC in the elevated process: Wi-Fi keys and BitLocker recovery passwords can only be read there.
/// The result comes back in <see cref="EngineJobResult.ReportJson"/> as the capture, secrets included, so the caller has to
/// handle it as a secret (the window encrypts it into a customer sheet).
/// </summary>
public sealed record CaptureCustomerPcJobRequest : EngineJobRequest
{
    public bool IncludeInstalledPrograms { get; init; } = true;

    public bool IncludeThirdPartyDrivers { get; init; } = true;

    public bool ListWlanProfiles { get; init; } = true;

    /// <summary>Also read the Wi-Fi keys; the engine exports the profiles into a folder of its own and deletes them again.</summary>
    public bool IncludeWlanKeys { get; init; }

    public bool IncludeBitLockerRecoveryKeys { get; init; }

    public bool IncludeWindowsProductKey { get; init; }
}
