// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Workshop.Firmware;
using Bootrix.Core.Workshop.Hardware;
using Bootrix.Core.Workshop.Licensing;

namespace Bootrix.Core.Workshop.Advice;

public sealed record TargetPcAdvisorOptions
{
    /// <summary>Day the lifecycle statements refer to; defaults to the day the data was collected, then to today.</summary>
    public DateOnly? Today { get; init; }
}

/// <summary>
/// Turns what was measured into advice. The function is pure: the same <see cref="TargetPcInfo"/> and date always give the same
/// assessment, and it never reaches for the hardware. Whatever the data does not show is reported as unknown, not guessed.
/// </summary>
public static class TargetPcAdvisor
{
    private const int LifecycleWarningDays = 90;

    public static TargetPcAssessment Evaluate(TargetPcInfo info, TargetPcAdvisorOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(info);

        var today = options?.Today
            ?? (info.CollectedAt is { } collected ? DateOnly.FromDateTime(collected.UtcDateTime) : DateOnly.FromDateTime(DateTime.UtcNow));

        var checks = Windows11Requirements.Evaluate(info);
        var verdict = Windows11Requirements.Verdict(checks);
        var lacksModernInstructions = LacksModernInstructions(info.Cpu);

        var image = ImageAdvisor.Recommend(info, verdict, lacksModernInstructions, today);
        var boot = BootAdvisor.Advise(info);
        var bypasses = checks
            .Where(c => c.Status == CheckStatus.Fail && c.Bypass is not null)
            .Select(c => c.Bypass!.Value)
            .Distinct()
            .ToList();

        var messages = new List<AdvisorMessage>();
        AddDataNotes(info, messages);
        AddCpuNotes(info.Cpu, lacksModernInstructions, messages);
        AddImageNotes(image, verdict, messages);
        AddLicensingNotes(info, image, messages);
        AddBitLockerNotes(info.BitLocker, info.IsElevated, messages);
        AddMachineNotes(info, messages);
        AddLifecycleNotes(info.RunningSystem, today, messages);
        AddStorageNotes(info.Disks, messages);

        return new TargetPcAssessment
        {
            AssessedOn = today,
            Windows11 = verdict,
            Windows11Checks = checks,
            Bypasses = bypasses,
            Image = image,
            DriverNeeds = DriverNeedAnalyzer.Analyze(info),
            Boot = boot,
            Messages = [.. messages.OrderByDescending(m => m.Severity)],
            SuggestedProfile = SuggestedProfileBuilder.Build(info, image, boot, bypasses),
        };
    }

    /// <summary>
    /// Windows 11 24H2 and later are reported to fail on processors without POPCNT or SSE4.2. That comes from community
    /// reports and not from a Microsoft statement, so the advisor treats it as a strong hint and words it that way.
    /// </summary>
    private static bool LacksModernInstructions(CpuInfo? cpu)
    {
        // Only 64-bit x86 matters: Windows 11 does not exist for 32-bit CPUs, and Arm has no such instruction sets.
        if (cpu?.Features is not { } features || ImageAdvisor.TargetArchitecture(cpu) != CpuArchitecture.X64)
        {
            return false;
        }

        return !features.HasFlag(CpuFeatures.Popcnt) || !features.HasFlag(CpuFeatures.Sse42);
    }

    private static void AddDataNotes(TargetPcInfo info, List<AdvisorMessage> messages)
    {
        if (info.Cpu is null && info.Firmware is null && info.Memory is null && info.Disks is null && info.Devices is null)
        {
            messages.Add(new AdvisorMessage(AdvisorKeys.DataNothing, AdvisorSeverity.Warning));
            return;
        }

        if (info.Issues.Count > 0)
        {
            messages.Add(new AdvisorMessage(AdvisorKeys.DataIncomplete, AdvisorSeverity.Info, string.Join(", ", info.Issues.Select(i => i.Source).Distinct())));
        }

        if (info.IsElevated == false)
        {
            messages.Add(new AdvisorMessage(AdvisorKeys.DataNotElevated, AdvisorSeverity.Info));
        }
    }

    private static void AddCpuNotes(CpuInfo? cpu, bool lacksModernInstructions, List<AdvisorMessage> messages)
    {
        if (cpu is null)
        {
            return;
        }

        switch (cpu.Architecture)
        {
            case CpuArchitecture.Arm64:
                messages.Add(new AdvisorMessage(AdvisorKeys.CpuArm64, AdvisorSeverity.Info));
                if (CpuNameHeuristics.IsNewArmPlatform(cpu.Name))
                {
                    messages.Add(new AdvisorMessage(AdvisorKeys.CpuNewPlatform, AdvisorSeverity.Info, cpu.Name));
                }

                break;
            case CpuArchitecture.Arm:
                messages.Add(new AdvisorMessage(AdvisorKeys.CpuArm32, AdvisorSeverity.Critical));
                break;
            case CpuArchitecture.X86 when cpu.Is64BitCapable == false:
                messages.Add(new AdvisorMessage(AdvisorKeys.CpuOnly32Bit, AdvisorSeverity.Warning));
                break;
            case CpuArchitecture.X86 when cpu.Is64BitCapable == true:
                messages.Add(new AdvisorMessage(AdvisorKeys.CpuRunning32Bit, AdvisorSeverity.Info));
                break;
        }

        if (lacksModernInstructions)
        {
            var missing = new List<string>();
            if (cpu.Features is { } features)
            {
                AddIfMissing(features, CpuFeatures.Popcnt, "POPCNT", missing);
                AddIfMissing(features, CpuFeatures.Sse42, "SSE4.2", missing);
            }

            messages.Add(new AdvisorMessage(AdvisorKeys.CpuNoModernInstructions, AdvisorSeverity.Warning, string.Join(", ", missing)));
        }
    }

    private static void AddIfMissing(CpuFeatures features, CpuFeatures flag, string name, List<string> missing)
    {
        if (!features.HasFlag(flag))
        {
            missing.Add(name);
        }
    }

    private static void AddImageNotes(ImageRecommendation? image, Windows11Verdict verdict, List<AdvisorMessage> messages)
    {
        if (image is null)
        {
            return;
        }

        if (image.Product == WindowsProduct.Windows10)
        {
            messages.Add(new AdvisorMessage(AdvisorKeys.Windows10EndOfSupport, AdvisorSeverity.Warning));
        }
        else if (verdict == Windows11Verdict.NeedsBypass)
        {
            messages.Add(new AdvisorMessage(AdvisorKeys.BypassUnsupported, AdvisorSeverity.Warning));
        }
    }

    private static void AddLicensingNotes(TargetPcInfo info, ImageRecommendation? image, List<AdvisorMessage> messages)
    {
        var license = info.OemLicense;
        if (license is null)
        {
            return;
        }

        if (!license.HasFirmwareKey)
        {
            messages.Add(new AdvisorMessage(AdvisorKeys.LicensingNoFirmwareKey, AdvisorSeverity.Info));
        }
        else if (WindowsEditions.Canonical(license.EditionId) is null)
        {
            messages.Add(new AdvisorMessage(AdvisorKeys.LicensingEditionUnknown, AdvisorSeverity.Warning));
        }
        else if (image?.EditionSource == EditionSource.FirmwareKey)
        {
            messages.Add(new AdvisorMessage(AdvisorKeys.LicensingEditionFromFirmware, AdvisorSeverity.Info, image.Edition ?? image.EditionId));
        }

        if (image?.EditionSource == EditionSource.RunningSystem)
        {
            messages.Add(new AdvisorMessage(AdvisorKeys.LicensingEditionFromRunning, AdvisorSeverity.Info, image.Edition ?? image.EditionId));
        }
    }

    private static void AddBitLockerNotes(IReadOnlyList<BitLockerVolumeInfo>? volumes, bool? elevated, List<AdvisorMessage> messages)
    {
        if (volumes is null)
        {
            // Reading BitLocker needs administrator rights; without them nothing can be said, which is not the same as "off".
            if (elevated == false)
            {
                messages.Add(new AdvisorMessage(AdvisorKeys.BitLockerUnknown, AdvisorSeverity.Info));
            }

            return;
        }

        var locked = volumes.Where(v => v.IsLocked == true).Select(v => v.Volume).ToList();
        if (locked.Count > 0)
        {
            messages.Add(new AdvisorMessage(AdvisorKeys.BitLockerLocked, AdvisorSeverity.Critical, string.Join(", ", locked)));
        }

        var active = volumes.Where(v => v.IsEncryptedOrConverting && v.IsLocked != true).Select(v => v.Volume).ToList();
        if (active.Count > 0)
        {
            messages.Add(new AdvisorMessage(AdvisorKeys.BitLockerActive, AdvisorSeverity.Warning, string.Join(", ", active)));
        }
    }

    private static void AddMachineNotes(TargetPcInfo info, List<AdvisorMessage> messages)
    {
        var machine = info.Machine;
        if (machine is { VirtualMachine: not (VirtualMachineKind.None or VirtualMachineKind.Unknown) })
        {
            messages.Add(new AdvisorMessage(AdvisorKeys.MachineVirtual, AdvisorSeverity.Info, machine.VirtualMachine.ToString()));
            if (machine.VirtualMachine == VirtualMachineKind.HyperV && info.Firmware?.Type == FirmwareType.Bios)
            {
                messages.Add(new AdvisorMessage(AdvisorKeys.MachineHyperVGeneration1, AdvisorSeverity.Info));
            }
        }

        if (machine?.Chassis == ChassisKind.Server)
        {
            messages.Add(new AdvisorMessage(AdvisorKeys.MachineServerHardware, AdvisorSeverity.Warning));
        }

        if (info.RunningSystem?.IsServer == true)
        {
            messages.Add(new AdvisorMessage(AdvisorKeys.MachineRunningServer, AdvisorSeverity.Info));
        }
    }

    private static void AddLifecycleNotes(RunningSystemInfo? running, DateOnly today, List<AdvisorMessage> messages)
    {
        if (running is null || running.IsServer == true || running.IsWindows11 is null)
        {
            return;
        }

        var product = running.IsWindows11 == true ? WindowsProduct.Windows11 : WindowsProduct.Windows10;
        var release = WindowsReleaseTable.Find(product, running.DisplayVersion);
        if (release is null || release.EndOfServicing > today.AddDays(LifecycleWarningDays))
        {
            return;
        }

        messages.Add(new AdvisorMessage(AdvisorKeys.LifecycleRunningVersionEnding, AdvisorSeverity.Info, $"{release.ProductName} {release.Version}", release.EndOfServicing));
    }

    private static void AddStorageNotes(IReadOnlyList<DiskInfo>? disks, List<AdvisorMessage> messages)
    {
        if (disks is not null && !disks.Any(d => d.IsInternal))
        {
            messages.Add(new AdvisorMessage(AdvisorKeys.StorageNoDisk, AdvisorSeverity.Warning));
        }
    }
}
