// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Images;
using Bootrix.Core.Model;
using Bootrix.Core.Partitioning;

namespace Bootrix.Core.Planning;

/// <summary>
/// Plans a sector-by-sector copy. The image brings its own partition table and boot code, so the
/// only thing Bootrix may add is a persistence partition behind it.
/// </summary>
internal static class RawCopyPlan
{
    public static MediaPlan Create(PlanContext ctx)
    {
        var image = ctx.Image;
        var floppy = ctx.Device.Medium == DeviceMedium.Floppy;
        var required = Math.Max(0, image.TotalBytes);
        if (ctx.DeviceBytes < required)
        {
            throw ctx.DeviceTooSmall(required);
        }

        AddWarnings(ctx, floppy);

        var partitions = new List<PlannedPartition>();
        var wanted = ctx.Target.PersistenceMegabytes * PlanLimits.Mib;
        if (wanted > 0)
        {
            var persistence = PlanPersistence(ctx, wanted);
            if (persistence is not null)
            {
                partitions.Add(persistence);
            }
        }

        return new MediaPlan
        {
            Scheme = PartitionScheme.Auto,
            WriteMethod = WriteMethod.RawCopy,
            BootMethod = BootMethod.ImageNative,
            Firmware = ctx.Target.Firmware,
            Superfloppy = floppy,
            Partitions = partitions,
            NeedsPersistencePartition = partitions.Count > 0,
            SectorSize = ctx.SectorSize,
            DeviceBytes = ctx.DeviceBytes,
            Warnings = ctx.Warnings,
        };
    }

    private static void AddWarnings(PlanContext ctx, bool floppy)
    {
        var image = ctx.Image;
        var hybrid = image.IsHybrid || image.Kind == ImageKind.LinuxHybrid;
        if (ctx.SectorSize != 512 && hybrid)
        {
            ctx.Warn(PlanWarningCodes.FourKnHybridImage);
        }

        if (image.Kind == ImageKind.LinuxHybrid && !floppy && ctx.Target.PersistenceMegabytes <= 0)
        {
            ctx.Warn(PlanWarningCodes.RawCopyReadOnlyMedia);
        }

        var firmware = ctx.Target.Firmware;
        if (FirmwareResolver.IncludesUefi(firmware) && !image.HasEfiBootFiles && !image.HasElToritoEfi)
        {
            ctx.Warn(PlanWarningCodes.NoEfiBootFiles);
        }

        if (FirmwareResolver.IncludesBios(firmware) && !image.HasBiosBootFiles && !image.HasElToritoBios)
        {
            ctx.Warn(PlanWarningCodes.NoBiosBootFiles);
        }
    }

    /// <summary>The persistence partition goes into the free space behind the image, leaving a mebibyte at the end for a relocated backup GPT.</summary>
    private static PlannedPartition? PlanPersistence(PlanContext ctx, long wanted)
    {
        var image = ctx.Image;
        if (image.Kind != ImageKind.LinuxHybrid || image.TotalBytes <= 0)
        {
            ctx.Warn(PlanWarningCodes.PersistenceUnsupported);
            return null;
        }

        if (wanted < PlanLimits.MinPersistenceBytes)
        {
            throw PlanContext.Unsupported(
                ErrorCode.PersistenceTooSmall, SizeText.Format(wanted), SizeText.Format(PlanLimits.MinPersistenceBytes));
        }

        var start = PartitionAlignment.AlignUp(image.TotalBytes, PlanLimits.Mib);
        var end = PartitionAlignment.AlignDown(ctx.DeviceBytes - PlanLimits.Mib, PlanLimits.Mib);
        var room = end - start;
        var granted = Math.Min(wanted, room);
        if (granted < PlanLimits.MinPersistenceBytes)
        {
            throw ctx.DeviceTooSmall(ctx.DeviceBytes - room + PlanLimits.MinPersistenceBytes);
        }

        if (granted < wanted)
        {
            ctx.Warn(PlanWarningCodes.PersistenceReduced, SizeText.Format(wanted), SizeText.Format(granted));
        }

        return new PlannedPartition
        {
            Role = PartitionRole.Persistence,
            StartBytes = start,
            LengthBytes = granted,
            FileSystem = FileSystemKind.Ext3,
            Label = LinuxFamilies.PersistenceLabel(image.Family),
            MbrType = MbrPartitionType.Linux,
            GptType = GptTypes.LinuxData,
        };
    }
}
