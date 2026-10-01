// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.FileSystems.Fat;
using Bootrix.Core.Model;
using Bootrix.Core.Partitioning;

namespace Bootrix.Core.Planning;

/// <summary>
/// Plans a medium with no partition table: the file system starts at LBA 0. That is how floppies
/// work and what some BIOSes expect from a stick in diskette emulation.
/// </summary>
internal static class SuperfloppyPlan
{
    public static MediaPlan Create(PlanContext ctx, WriteMethod method)
    {
        var floppy = ctx.Device.Medium == DeviceMedium.Floppy;
        if (method == WriteMethod.ApplyImage)
        {
            throw PlanContext.Unsupported(ErrorCode.WriteModeUnsupported, "WindowsToGo", ctx.Image.Kind);
        }

        if (ctx.Target.Scheme == PartitionScheme.Gpt)
        {
            const string reason = "a medium without partition table cannot use GPT";
            throw new BootrixException(ErrorCode.InvalidSpec, reason) { Arguments = [reason] };
        }

        if (ctx.Purpose == MediaPurpose.Dos && ctx.SectorSize != 512)
        {
            throw PlanContext.Unsupported(ErrorCode.SectorSizeUnsupported, ctx.SectorSize, "FreeDOS");
        }

        var size = ctx.DeviceBytes;
        var choice = floppy ? FloppyFileSystem(ctx, size) : FatOnly(ctx, size);
        var fileSystem = choice.FileSystem;

        var limit = FileSystemRules.MaxBytes(fileSystem, ctx.SectorSize, ctx.Purpose == MediaPurpose.Dos);
        var length = PartitionAlignment.AlignDown(Math.Min(size, limit), ctx.SectorSize);
        var image = ctx.Image;
        var required = image.TotalBytes > 0 ? image.TotalBytes + PlanLimits.ExtractOverhead(image.TotalBytes) : 0;
        if (length < required)
        {
            throw length < size
                ? PlanContext.Unsupported(ErrorCode.FileSystemTooSmall, fileSystem, SizeText.Format(length))
                : ctx.DeviceTooSmall(required);
        }

        if (length < size)
        {
            ctx.Warn(PlanWarningCodes.CapacityNotUsed, SizeText.Format(length), SizeText.Format(size - length));
        }

        if (ctx.Target.PersistenceMegabytes > 0)
        {
            ctx.Warn(PlanWarningCodes.PersistenceUnsupported);
        }

        var firmware = FirmwareResolver.Resolve(ctx);
        if (!floppy)
        {
            ctx.Warn(PlanWarningCodes.SuperfloppyBoot);
        }

        return new MediaPlan
        {
            Scheme = PartitionScheme.Auto,
            WriteMethod = method,
            BootMethod = BootMethodFor(ctx, firmware),
            Firmware = firmware,
            Superfloppy = true,
            Partitions =
            [
                new PlannedPartition
                {
                    Role = PartitionRole.Main,
                    StartBytes = 0,
                    LengthBytes = length,
                    FileSystem = fileSystem,
                    Label = ctx.Target.Label ?? image.VolumeLabel,
                    ClusterSizeBytes = ctx.Target.ClusterSizeBytes,
                },
            ],
            SplitWim = choice.SplitWim,
            SectorSize = ctx.SectorSize,
            DeviceBytes = size,
            Warnings = ctx.Warnings,
        };
    }

    private static FileSystemChoice FloppyFileSystem(PlanContext ctx, long size)
    {
        var requested = ctx.Target.FileSystem;
        if (requested is not (FileSystemKind.Auto or FileSystemKind.Fat12))
        {
            throw PlanContext.Unsupported(ErrorCode.FileSystemUnsupported, requested, "Floppy");
        }

        if (FloppyPreset.FromSize(size) is null)
        {
            ctx.Warn(PlanWarningCodes.NonStandardFloppySize, SizeText.Format(size));
        }

        return new FileSystemChoice(FileSystemKind.Fat12, false);
    }

    private static FileSystemChoice FatOnly(PlanContext ctx, long size)
    {
        var choice = FileSystemRules.Choose(ctx, size, ctx.Target.ClusterSizeBytes);
        return FileSystemRules.IsFat(choice.FileSystem)
            ? choice
            : throw PlanContext.Unsupported(ErrorCode.FileSystemUnsupported, choice.FileSystem, "Superfloppy");
    }

    private static BootMethod BootMethodFor(PlanContext ctx, TargetFirmware firmware)
    {
        var method = BootMethod.None;
        if (FirmwareResolver.IncludesBios(firmware))
        {
            method |= ctx.Purpose switch
            {
                MediaPurpose.Windows => BootMethod.WindowsBootmgrBios,
                MediaPurpose.Linux => BootMethod.SyslinuxMbr,
                MediaPurpose.Dos => BootMethod.FreeDos,
                _ => BootMethod.None,
            };
        }

        if (FirmwareResolver.IncludesUefi(firmware))
        {
            method |= BootMethod.UefiNative;
        }

        return method;
    }
}
