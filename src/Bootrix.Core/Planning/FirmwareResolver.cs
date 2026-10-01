// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images;
using Bootrix.Core.Model;

namespace Bootrix.Core.Planning;

internal static class FirmwareResolver
{
    public static bool IncludesBios(TargetFirmware firmware) => firmware is TargetFirmware.Bios or TargetFirmware.BiosAndUefi;

    public static bool IncludesUefi(TargetFirmware firmware) => firmware is TargetFirmware.Uefi or TargetFirmware.BiosAndUefi;

    /// <summary>
    /// Turns the requested firmware (possibly "auto") into what the media can actually boot,
    /// warning where the image lacks files for a firmware the user asked for.
    /// </summary>
    public static TargetFirmware Resolve(PlanContext ctx)
    {
        var image = ctx.Image;
        var requested = ctx.Target.Firmware;

        switch (ctx.Purpose)
        {
            case MediaPurpose.Data:
                return TargetFirmware.Auto;
            case MediaPurpose.Dos:
                if (IncludesUefi(requested) && !IncludesBios(requested))
                {
                    ctx.Warn(PlanWarningCodes.NoEfiBootFiles);
                }

                return TargetFirmware.Bios;
        }

        var hasBios = image.HasBiosBootFiles || image.HasElToritoBios;
        var hasEfi = image.HasEfiBootFiles || image.HasElToritoEfi;
        if (ctx.Purpose == MediaPurpose.Windows && !hasBios && !hasEfi)
        {
            // An incomplete profile; every Windows medium carries bootmgr and an EFI loader.
            hasBios = hasEfi = true;
        }

        var arm = image.Arch == WindowsArch.Arm64;
        if (arm)
        {
            hasBios = false;
        }

        if (requested == TargetFirmware.Auto)
        {
            return (hasBios, hasEfi) switch
            {
                (true, true) => TargetFirmware.BiosAndUefi,
                (false, true) => TargetFirmware.Uefi,
                (true, false) => TargetFirmware.Bios,
                _ => TargetFirmware.BiosAndUefi,
            };
        }

        var wantBios = IncludesBios(requested);
        var wantUefi = IncludesUefi(requested);
        if (wantBios && arm)
        {
            ctx.Warn(PlanWarningCodes.ArmHasNoBios);
            wantBios = false;
            wantUefi = true;
        }

        if (wantUefi && !hasEfi)
        {
            ctx.Warn(PlanWarningCodes.NoEfiBootFiles);
        }

        if (wantBios && !hasBios)
        {
            ctx.Warn(PlanWarningCodes.NoBiosBootFiles);
        }

        return (wantBios, wantUefi) switch
        {
            (true, true) => TargetFirmware.BiosAndUefi,
            (true, false) => TargetFirmware.Bios,
            _ => TargetFirmware.Uefi,
        };
    }
}
