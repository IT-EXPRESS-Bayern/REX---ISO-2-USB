// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Model;

namespace Bootrix.Core.Planning;

/// <summary>What the medium is meant to do, before any byte offsets are decided.</summary>
internal sealed record MediumIntent(
    TargetFirmware Firmware,
    PartitionScheme Scheme,
    bool Legacy,
    BootMethod BiosMethod,
    bool WindowsToGo)
{
    public bool Bios => FirmwareResolver.IncludesBios(Firmware);

    public bool Uefi => FirmwareResolver.IncludesUefi(Firmware);
}

/// <summary>
/// Plans a medium that gets a partition table and formatted partitions: Windows setup media,
/// Linux in ISO mode, FreeDOS, data sticks and Windows To Go.
/// </summary>
internal static class FilePlan
{
    public static MediaPlan Create(PlanContext ctx, WriteMethod method)
    {
        if (ctx.Device.Medium == DeviceMedium.Floppy || ctx.Target.Superfloppy)
        {
            return SuperfloppyPlan.Create(ctx, method);
        }

        if (ctx.Purpose == MediaPurpose.Dos && ctx.SectorSize != 512)
        {
            throw PlanContext.Unsupported(ErrorCode.SectorSizeUnsupported, ctx.SectorSize, "FreeDOS");
        }

        var intent = Decide(ctx, method);
        var layout = FileLayout.Build(ctx, intent);

        return new MediaPlan
        {
            Scheme = intent.Scheme,
            WriteMethod = method,
            BootMethod = ComposeBootMethod(intent, layout.UsesUefiNtfs),
            Firmware = intent.Firmware,
            LegacyBios = intent.Legacy,
            Partitions = layout.Partitions,
            UsesUefiNtfs = layout.UsesUefiNtfs,
            SplitWim = layout.SplitWim,
            NeedsPersistencePartition = layout.HasPersistence,
            WindowsToGo = intent.WindowsToGo,
            SectorSize = ctx.SectorSize,
            DeviceBytes = ctx.DeviceBytes,
            Warnings = ctx.Warnings,
        };
    }

    private static MediumIntent Decide(PlanContext ctx, WriteMethod method)
    {
        var firmware = FirmwareResolver.Resolve(ctx);
        var bios = FirmwareResolver.IncludesBios(firmware);
        var legacy = ResolveLegacy(ctx, bios);
        var scheme = SchemeResolver.Resolve(ctx, firmware, legacy);
        var biosMethod = bios ? BiosMethod(ctx) : BootMethod.None;

        if (bios && ctx.SectorSize != 512)
        {
            ctx.Warn(PlanWarningCodes.FourKnBios);
        }

        if (scheme == PartitionScheme.Gpt && biosMethod == BootMethod.WindowsBootmgrBios)
        {
            ctx.Warn(PlanWarningCodes.BiosOnGpt);
        }

        return new MediumIntent(firmware, scheme, legacy, biosMethod, method == WriteMethod.ApplyImage);
    }

    private static bool ResolveLegacy(PlanContext ctx, bool bios)
    {
        if (!ctx.Target.LegacyBiosFixes)
        {
            return false;
        }

        if (ctx.SectorSize != 512)
        {
            ctx.Warn(PlanWarningCodes.FourKnLegacyIgnored);
            return false;
        }

        if (!bios)
        {
            ctx.Warn(PlanWarningCodes.LegacyNeedsBios);
            return false;
        }

        return true;
    }

    private static BootMethod BiosMethod(PlanContext ctx) => ctx.Purpose switch
    {
        MediaPurpose.Windows => BootMethod.WindowsBootmgrBios,
        MediaPurpose.Linux => LinuxFamilies.UsesGrubForBios(ctx.Image.Family) ? BootMethod.Grub : BootMethod.SyslinuxMbr,
        MediaPurpose.Dos => BootMethod.FreeDos,
        _ => BootMethod.None,
    };

    private static BootMethod ComposeBootMethod(MediumIntent intent, bool usesUefiNtfs)
    {
        var method = intent.BiosMethod;
        if (intent.Uefi)
        {
            method |= usesUefiNtfs ? BootMethod.UefiNtfs : BootMethod.UefiNative;
        }

        return method;
    }
}
