// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Workshop.Hardware;

namespace Bootrix.Core.Workshop.Advice;

/// <summary>
/// Checks the data against the minimum hardware requirements Microsoft publishes for Windows 11: a 64-bit processor with two cores at
/// 1 GHz, 4 GB RAM, 64 GB storage, UEFI with Secure Boot capability and TPM 2.0 (DirectX 12 graphics is not measured).
/// </summary>
internal static class Windows11Requirements
{
    private const long GiB = 1L << 30;
    private const long MinimumRam = 4 * GiB;
    private const long MinimumStorage = 64 * GiB;

    // A "64 GB" drive holds about 59.6 GiB, and "4 GB" of RAM shows up as less once the firmware reserved its share.
    // Within these margins the result is reported as unknown instead of guessing which side Setup lands on.
    private const long RamMargin = 3 * GiB;
    private const long StorageMargin = 58 * GiB;
    private const int MinimumCores = 2;
    private const int MinimumClockMhz = 1000;

    public static IReadOnlyList<RequirementCheck> Evaluate(TargetPcInfo info) =>
    [
        Architecture(info),
        Cores(info.Cpu),
        Clock(info.Cpu),
        CpuModel(info.Cpu),
        Ram(info.Memory),
        Storage(info.Disks),
        Firmware(info),
        SecureBoot(info),
        Tpm(info.Tpm),
        new(Windows11Requirement.Graphics, CheckStatus.NotChecked, Info(AdvisorKeys.GraphicsNotChecked)),
    ];

    public static Windows11Verdict Verdict(IReadOnlyList<RequirementCheck> checks)
    {
        var failed = checks.Where(c => c.Status == CheckStatus.Fail).ToList();
        if (failed.Any(c => c.Bypass is null))
        {
            return Windows11Verdict.NotPossible;
        }

        if (failed.Count > 0)
        {
            return Windows11Verdict.NeedsBypass;
        }

        return checks.Any(c => c.Status == CheckStatus.Unknown) ? Windows11Verdict.Unknown : Windows11Verdict.LikelySupported;
    }

    private static RequirementCheck Architecture(TargetPcInfo info)
    {
        var cpu = info.Cpu;
        if (cpu is null)
        {
            return Unknown(Windows11Requirement.Architecture, AdvisorKeys.ArchitectureUnknown);
        }

        var name = cpu.Architecture.ToString();
        var sixtyFourBit = cpu.Architecture switch
        {
            CpuArchitecture.X64 or CpuArchitecture.Arm64 => true,
            CpuArchitecture.Arm => false,
            _ => cpu.Is64BitCapable,
        };

        return sixtyFourBit switch
        {
            true => new(Windows11Requirement.Architecture, CheckStatus.Pass, Info(AdvisorKeys.ArchitecturePass, name)),
            false => new(Windows11Requirement.Architecture, CheckStatus.Fail, new(AdvisorKeys.ArchitectureFail, AdvisorSeverity.Critical, name)),
            _ => Unknown(Windows11Requirement.Architecture, AdvisorKeys.ArchitectureUnknown),
        };
    }

    private static RequirementCheck Cores(CpuInfo? cpu)
    {
        var physical = cpu?.PhysicalCores;
        var logical = cpu?.LogicalProcessors;

        // Without the physical count, four logical processors still mean at least two cores; two or three are ambiguous.
        var enough = physical is { } p ? p >= MinimumCores : logical switch
        {
            >= 4 => true,
            1 => false,
            _ => (bool?)null,
        };

        return enough switch
        {
            true => new(Windows11Requirement.Cores, CheckStatus.Pass, Info(AdvisorKeys.CoresPass, physical ?? logical)),
            false => Failure(Windows11Requirement.Cores, AdvisorKeys.CoresFail, LabConfigBypass.Cpu, physical ?? logical),
            _ => Unknown(Windows11Requirement.Cores, AdvisorKeys.CoresUnknown, logical),
        };
    }

    private static RequirementCheck Clock(CpuInfo? cpu)
    {
        if (cpu?.MaxClockMhz is not { } mhz)
        {
            return new(Windows11Requirement.Clock, CheckStatus.NotChecked, Info(AdvisorKeys.ClockNotChecked));
        }

        var ghz = mhz / 1000.0;
        return mhz >= MinimumClockMhz
            ? new(Windows11Requirement.Clock, CheckStatus.Pass, Info(AdvisorKeys.ClockPass, ghz))
            : Failure(Windows11Requirement.Clock, AdvisorKeys.ClockFail, LabConfigBypass.Cpu, ghz);
    }

    private static RequirementCheck CpuModel(CpuInfo? cpu) =>
        cpu?.Architecture is not CpuArchitecture.Arm64 && CpuNameHeuristics.LooksUnsupportedByWindows11(cpu?.Name)
            ? Failure(Windows11Requirement.CpuModel, AdvisorKeys.CpuModelFail, LabConfigBypass.Cpu, cpu!.Name)
            : new(Windows11Requirement.CpuModel, CheckStatus.NotChecked, Info(AdvisorKeys.CpuModelNotChecked));

    private static RequirementCheck Ram(MemoryInfo? memory)
    {
        if (memory?.InstalledBytes is { } installed)
        {
            return installed >= MinimumRam
                ? new(Windows11Requirement.Ram, CheckStatus.Pass, Info(AdvisorKeys.RamPass, Gib(installed)))
                : Failure(Windows11Requirement.Ram, AdvisorKeys.RamFail, LabConfigBypass.Ram, Gib(installed));
        }

        if (memory?.UsableBytes is not { } usable)
        {
            return Unknown(Windows11Requirement.Ram, AdvisorKeys.RamUnknown);
        }

        if (usable >= MinimumRam)
        {
            return new(Windows11Requirement.Ram, CheckStatus.Pass, Info(AdvisorKeys.RamPass, Gib(usable)));
        }

        return usable >= RamMargin
            ? Unknown(Windows11Requirement.Ram, AdvisorKeys.RamBorderline, Gib(usable))
            : Failure(Windows11Requirement.Ram, AdvisorKeys.RamFail, LabConfigBypass.Ram, Gib(usable));
    }

    private static RequirementCheck Storage(IReadOnlyList<DiskInfo>? disks)
    {
        var largest = disks?.Where(d => d.IsInternal).Select(d => d.SizeBytes).DefaultIfEmpty(0).Max() ?? 0;
        if (largest <= 0)
        {
            return Unknown(Windows11Requirement.Storage, AdvisorKeys.StorageUnknown);
        }

        if (largest >= MinimumStorage)
        {
            return new(Windows11Requirement.Storage, CheckStatus.Pass, Info(AdvisorKeys.StoragePass, Gib(largest)));
        }

        return largest >= StorageMargin
            ? Unknown(Windows11Requirement.Storage, AdvisorKeys.StorageBorderline, Gib(largest))
            : Failure(Windows11Requirement.Storage, AdvisorKeys.StorageFail, LabConfigBypass.Storage, Gib(largest));
    }

    private static RequirementCheck Firmware(TargetPcInfo info)
    {
        // Windows on Arm always starts through UEFI, even when the firmware type could not be read.
        var type = info.Firmware?.Type ?? FirmwareType.Unknown;
        if (type == FirmwareType.Unknown && info.Cpu?.Architecture == CpuArchitecture.Arm64)
        {
            type = FirmwareType.Uefi;
        }

        return type switch
        {
            FirmwareType.Uefi => new(Windows11Requirement.Firmware, CheckStatus.Pass, Info(AdvisorKeys.FirmwarePass)),
            FirmwareType.Bios => Failure(Windows11Requirement.Firmware, AdvisorKeys.FirmwareFail, LabConfigBypass.SecureBoot),
            _ => Unknown(Windows11Requirement.Firmware, AdvisorKeys.FirmwareUnknown),
        };
    }

    private static RequirementCheck SecureBoot(TargetPcInfo info)
    {
        var firmware = info.Firmware;
        if (firmware?.Type == FirmwareType.Bios)
        {
            return Failure(Windows11Requirement.SecureBoot, AdvisorKeys.SecureBootFail, LabConfigBypass.SecureBoot);
        }

        // Either state proves the platform supports Secure Boot: legacy firmware has no SecureBoot variable at all.
        return firmware?.SecureBoot switch
        {
            SecureBootState.On => new(Windows11Requirement.SecureBoot, CheckStatus.Pass, Info(AdvisorKeys.SecureBootPassOn)),
            SecureBootState.Off => new(Windows11Requirement.SecureBoot, CheckStatus.Pass, Info(AdvisorKeys.SecureBootPassOff)),
            _ => Unknown(Windows11Requirement.SecureBoot, AdvisorKeys.SecureBootUnknown),
        };
    }

    private static RequirementCheck Tpm(TpmInfo? tpm)
    {
        if (tpm?.Present is not { } present)
        {
            return Unknown(Windows11Requirement.Tpm, AdvisorKeys.TpmUnknown);
        }

        if (!present)
        {
            return Failure(Windows11Requirement.Tpm, AdvisorKeys.TpmFailMissing, LabConfigBypass.Tpm);
        }

        return tpm.IsVersion20 switch
        {
            null => Unknown(Windows11Requirement.Tpm, AdvisorKeys.TpmUnknown),
            false => Failure(Windows11Requirement.Tpm, AdvisorKeys.TpmFailVersion, LabConfigBypass.Tpm, tpm.SpecVersion),
            true when tpm.IsEnabled == false => Failure(Windows11Requirement.Tpm, AdvisorKeys.TpmFailDisabled, LabConfigBypass.Tpm),
            true => new(Windows11Requirement.Tpm, CheckStatus.Pass, Info(AdvisorKeys.TpmPass, tpm.SpecVersion)),
        };
    }

    private static double Gib(long bytes) => Math.Round((double)bytes / GiB, 1);

    private static AdvisorMessage Info(string key, params object?[] arguments) => new(key, AdvisorSeverity.Info, arguments);

    private static RequirementCheck Unknown(Windows11Requirement requirement, string key, params object?[] arguments) =>
        new(requirement, CheckStatus.Unknown, Info(key, arguments));

    private static RequirementCheck Failure(Windows11Requirement requirement, string key, LabConfigBypass bypass, params object?[] arguments) =>
        new(requirement, CheckStatus.Fail, new AdvisorMessage(key, AdvisorSeverity.Warning, arguments), bypass);
}
