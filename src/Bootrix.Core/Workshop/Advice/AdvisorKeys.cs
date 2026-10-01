// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Workshop.Advice;

/// <summary>Resource keys of all texts the advisor and the capture produce. A test checks that each one exists in German and English.</summary>
public static class AdvisorKeys
{
    private const string Prefix = "Workshop.";

    public const string ArchitecturePass = Prefix + "Req.Architecture.Pass";
    public const string ArchitectureFail = Prefix + "Req.Architecture.Fail";
    public const string ArchitectureUnknown = Prefix + "Req.Architecture.Unknown";
    public const string CoresPass = Prefix + "Req.Cores.Pass";
    public const string CoresFail = Prefix + "Req.Cores.Fail";
    public const string CoresUnknown = Prefix + "Req.Cores.Unknown";
    public const string ClockPass = Prefix + "Req.Clock.Pass";
    public const string ClockFail = Prefix + "Req.Clock.Fail";
    public const string ClockNotChecked = Prefix + "Req.Clock.NotChecked";
    public const string CpuModelFail = Prefix + "Req.CpuModel.Fail";
    public const string CpuModelNotChecked = Prefix + "Req.CpuModel.NotChecked";
    public const string RamPass = Prefix + "Req.Ram.Pass";
    public const string RamFail = Prefix + "Req.Ram.Fail";
    public const string RamBorderline = Prefix + "Req.Ram.Borderline";
    public const string RamUnknown = Prefix + "Req.Ram.Unknown";
    public const string StoragePass = Prefix + "Req.Storage.Pass";
    public const string StorageFail = Prefix + "Req.Storage.Fail";
    public const string StorageBorderline = Prefix + "Req.Storage.Borderline";
    public const string StorageUnknown = Prefix + "Req.Storage.Unknown";
    public const string FirmwarePass = Prefix + "Req.Firmware.Pass";
    public const string FirmwareFail = Prefix + "Req.Firmware.Fail";
    public const string FirmwareUnknown = Prefix + "Req.Firmware.Unknown";
    public const string SecureBootPassOn = Prefix + "Req.SecureBoot.PassOn";
    public const string SecureBootPassOff = Prefix + "Req.SecureBoot.PassOff";
    public const string SecureBootFail = Prefix + "Req.SecureBoot.Fail";
    public const string SecureBootUnknown = Prefix + "Req.SecureBoot.Unknown";
    public const string TpmPass = Prefix + "Req.Tpm.Pass";
    public const string TpmFailMissing = Prefix + "Req.Tpm.FailMissing";
    public const string TpmFailVersion = Prefix + "Req.Tpm.FailVersion";
    public const string TpmFailDisabled = Prefix + "Req.Tpm.FailDisabled";
    public const string TpmUnknown = Prefix + "Req.Tpm.Unknown";
    public const string GraphicsNotChecked = Prefix + "Req.Graphics.NotChecked";

    public const string DataNothing = Prefix + "Data.Nothing";
    public const string DataIncomplete = Prefix + "Data.Incomplete";
    public const string DataNotElevated = Prefix + "Data.NotElevated";

    public const string CpuNoModernInstructions = Prefix + "Cpu.NoModernInstructions";
    public const string CpuArm64 = Prefix + "Cpu.Arm64";
    public const string CpuArm32 = Prefix + "Cpu.Arm32";
    public const string CpuOnly32Bit = Prefix + "Cpu.Only32Bit";
    public const string CpuRunning32Bit = Prefix + "Cpu.Running32Bit";
    public const string CpuNewPlatform = Prefix + "Cpu.NewPlatform";

    public const string BootSecureBootSignedOnly = Prefix + "Boot.SecureBootSignedOnly";
    public const string BootCertificate2023 = Prefix + "Boot.Certificate2023";
    public const string BootCertificate2011 = Prefix + "Boot.Certificate2011";
    public const string BootBiosMode = Prefix + "Boot.BiosMode";
    public const string BootBiosLargeDisk = Prefix + "Boot.BiosLargeDisk";

    public const string LicensingEditionFromFirmware = Prefix + "Licensing.EditionFromFirmware";
    public const string LicensingEditionFromRunning = Prefix + "Licensing.EditionFromRunning";
    public const string LicensingEditionUnknown = Prefix + "Licensing.EditionUnknown";
    public const string LicensingNoFirmwareKey = Prefix + "Licensing.NoFirmwareKey";

    public const string BitLockerActive = Prefix + "BitLocker.Active";
    public const string BitLockerLocked = Prefix + "BitLocker.Locked";
    public const string BitLockerUnknown = Prefix + "BitLocker.Unknown";

    public const string MachineVirtual = Prefix + "Machine.Virtual";
    public const string MachineHyperVGeneration1 = Prefix + "Machine.HyperVGeneration1";
    public const string MachineServerHardware = Prefix + "Machine.ServerHardware";
    public const string MachineRunningServer = Prefix + "Machine.RunningServer";

    public const string StorageNoDisk = Prefix + "Storage.NoDisk";
    public const string LifecycleRunningVersionEnding = Prefix + "Lifecycle.RunningVersionEnding";
    public const string Windows10EndOfSupport = Prefix + "Windows10.EndOfSupport";
    public const string AlternativeWindows10 = Prefix + "Alternative.Windows10";
    public const string AlternativeLinux = Prefix + "Alternative.Linux";
    public const string BypassUnsupported = Prefix + "Bypass.Unsupported";

    public const string DriverIntelVmd = Prefix + "Driver.IntelVmd";
    public const string DriverIntelRstRaid = Prefix + "Driver.IntelRstRaid";
    public const string DriverAmdRaid = Prefix + "Driver.AmdRaid";
    public const string DriverHardwareRaid = Prefix + "Driver.HardwareRaid";
    public const string DriverVirtualStorage = Prefix + "Driver.VirtualStorage";
    public const string DriverNetworkNoDriver = Prefix + "Driver.NetworkNoDriver";
    public const string DriverNetworkVendor = Prefix + "Driver.NetworkVendor";
    public const string DriverNetworkVirtual = Prefix + "Driver.NetworkVirtual";
    public const string DriverWirelessNoDriver = Prefix + "Driver.WirelessNoDriver";
    public const string DriverWirelessVendor = Prefix + "Driver.WirelessVendor";

    public const string CaptureSecretsIncluded = Prefix + "Capture.SecretsIncluded";
    public const string CaptureWlanExportFiles = Prefix + "Capture.WlanExportFiles";
    public const string CaptureWlanKeysProtected = Prefix + "Capture.WlanKeysProtected";
    public const string CaptureProductKeyMayBeGeneric = Prefix + "Capture.ProductKeyMayBeGeneric";
    public const string CaptureRecoveryKeyUnavailable = Prefix + "Capture.RecoveryKeyUnavailable";
}
