// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Model;
using Bootrix.Core.Profiles;

namespace Bootrix.Core.Workshop.Advice;

/// <summary>Fills a write job with what the check derived. Fields the check cannot know stay at their defaults.</summary>
internal static class SuggestedProfileBuilder
{
    public static JobSpec Build(
        TargetPcInfo info,
        ImageRecommendation? image,
        BootRecommendation boot,
        IReadOnlyCollection<LabConfigBypass> bypasses)
    {
        var running = info.RunningSystem;

        // Bypasses only exist for Windows 11 Setup; offering them with Windows 10 media would be noise.
        var bypass = image?.Product == WindowsProduct.Windows11 ? bypasses : [];

        // Windows PE reports its own language and the default time zone, not the customer's.
        var useRunningLocale = running?.IsWinPe != true;

        return new JobSpec
        {
            Name = info.Machine?.DisplayName ?? "",
            Kind = JobKind.WriteImage,
            Source = image?.CatalogQuery is { } query ? new ImageReference { CatalogQuery = query } : null,
            Target = new TargetOptions
            {
                Scheme = boot.Scheme,
                Firmware = boot.Firmware,
                FileSystem = boot.FileSystem,
            },
            Windows = new WindowsSetupOptions
            {
                BypassTpm = bypass.Contains(LabConfigBypass.Tpm),
                BypassSecureBoot = bypass.Contains(LabConfigBypass.SecureBoot),
                BypassRam = bypass.Contains(LabConfigBypass.Ram),
                BypassCpu = bypass.Contains(LabConfigBypass.Cpu),
                BypassStorage = bypass.Contains(LabConfigBypass.Storage),
                Edition = image?.Edition,
                UiLanguage = useRunningLocale ? running?.UiLanguage : null,
                TimeZone = useRunningLocale ? running?.TimeZoneId : null,
                BootCertificate = boot.Certificate,
            },
        };
    }
}
