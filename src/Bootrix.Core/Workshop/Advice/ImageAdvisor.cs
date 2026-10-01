// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bootrix.Core.Workshop.Hardware;
using Bootrix.Core.Workshop.Licensing;

namespace Bootrix.Core.Workshop.Advice;

/// <summary>Chooses product, release, architecture and edition of the Windows medium that fits the hardware.</summary>
internal static class ImageAdvisor
{
    public static ImageRecommendation? Recommend(TargetPcInfo info, Windows11Verdict verdict, bool lacksModernInstructions, DateOnly today)
    {
        var architecture = TargetArchitecture(info.Cpu);
        if (architecture is null)
        {
            return null;
        }

        var (release, alternatives) = ChooseRelease(info.Cpu, architecture.Value, verdict, lacksModernInstructions, today);
        if (release is null)
        {
            return null;
        }

        var editionId = ChooseEdition(info, out var source, out var channel);
        var language = info.RunningSystem?.IsWinPe == true ? null : info.RunningSystem?.UiLanguage;

        return new ImageRecommendation
        {
            Product = release.Product,
            Version = release.Version,
            Architecture = architecture.Value,
            Edition = WindowsEditions.ImageName(release.ProductName, editionId),
            EditionId = editionId,
            EditionSource = editionId is null ? EditionSource.None : source,
            Channel = source == EditionSource.FirmwareKey ? channel : null,
            Language = language,
            CatalogQuery = language is null ? null : string.Create(CultureInfo.InvariantCulture, $"{release.CatalogName}/{release.Version}/{language}/{ArchitectureToken(architecture.Value)}"),
            EndOfServicing = release.EndOfServicing,
            Alternatives = alternatives,
        };
    }

    /// <summary>The architecture of the medium, which follows what the CPU can run and not what is installed: 32-bit Windows on a 64-bit CPU gets x64 media.</summary>
    internal static CpuArchitecture? TargetArchitecture(CpuInfo? cpu) => cpu?.Architecture switch
    {
        CpuArchitecture.Arm64 => CpuArchitecture.Arm64,
        CpuArchitecture.X64 => CpuArchitecture.X64,
        CpuArchitecture.X86 or CpuArchitecture.Unknown when cpu.Is64BitCapable == true => CpuArchitecture.X64,
        CpuArchitecture.X86 when cpu.Is64BitCapable == false => CpuArchitecture.X86,
        _ => null,
    };

    internal static string ArchitectureToken(CpuArchitecture architecture) => architecture switch
    {
        CpuArchitecture.Arm64 => "arm64",
        CpuArchitecture.X86 => "x86",
        _ => "x64",
    };

    private static (WindowsRelease? Release, IReadOnlyList<AdvisorMessage> Alternatives) ChooseRelease(
        CpuInfo? cpu,
        CpuArchitecture architecture,
        Windows11Verdict verdict,
        bool lacksModernInstructions,
        DateOnly today)
    {
        // Without the instruction sets recent Windows 11 builds are reported not to start, and no setup bypass changes that.
        // Windows 10 is the only supported system left for such a PC, with Linux as the way out once its updates end.
        if (architecture == CpuArchitecture.X86 || verdict == Windows11Verdict.NotPossible || lacksModernInstructions)
        {
            return (WindowsReleaseTable.Recommended(WindowsProduct.Windows10, today), [Linux()]);
        }

        var newPlatform = architecture == CpuArchitecture.Arm64 && CpuNameHeuristics.IsNewArmPlatform(cpu?.Name);
        var release = newPlatform
            ? WindowsReleaseTable.ForNewDevices(WindowsProduct.Windows11, today)
            : null;
        release ??= WindowsReleaseTable.Recommended(WindowsProduct.Windows11, today);

        // A Windows 11 with bypassed checks is unsupported; the supported fallback is Windows 10 until its extended updates end.
        IReadOnlyList<AdvisorMessage> alternatives = verdict == Windows11Verdict.NeedsBypass
            ? [new AdvisorMessage(AdvisorKeys.AlternativeWindows10, AdvisorSeverity.Info), Linux()]
            : [];
        return (release, alternatives);
    }

    private static AdvisorMessage Linux() => new(AdvisorKeys.AlternativeLinux, AdvisorSeverity.Info);

    /// <summary>The firmware key decides the edition because Setup picks the matching one from it; the running Windows is only a fallback.</summary>
    private static string? ChooseEdition(TargetPcInfo info, out EditionSource source, out string? channel)
    {
        channel = null;
        source = EditionSource.None;

        if (WindowsEditions.Canonical(info.OemLicense?.EditionId) is { } fromFirmware)
        {
            source = EditionSource.FirmwareKey;
            channel = info.OemLicense?.Channel;
            return fromFirmware;
        }

        var running = info.RunningSystem;
        if (running?.IsServer != true && WindowsEditions.Canonical(running?.EditionId) is { } fromRunning)
        {
            source = EditionSource.RunningSystem;
            return fromRunning;
        }

        return null;
    }
}
