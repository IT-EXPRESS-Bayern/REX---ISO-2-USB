// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Model;
using Bootrix.Core.Partitioning;

namespace Bootrix.Core.Planning;

internal sealed record FileLayout(
    IReadOnlyList<PlannedPartition> Partitions,
    bool UsesUefiNtfs,
    bool SplitWim,
    bool HasPersistence)
{
    /// <summary>
    /// Places the partitions: system or BIOS-boot partitions in front, then the main partition,
    /// persistence and the UEFI:NTFS helper at the end. Everything is aligned to 1 MiB unless old
    /// BIOSes need the classic offset.
    /// </summary>
    public static FileLayout Build(PlanContext ctx, MediumIntent intent)
    {
        var ss = ctx.SectorSize;
        var space = FreeSpace(ctx, intent);
        var partitions = new List<PlannedPartition>();
        var cursor = space.Start;

        if (intent.WindowsToGo)
        {
            cursor = AddSystemPartitions(partitions, intent, cursor);
        }
        else if (intent.Scheme == PartitionScheme.Gpt && intent.BiosMethod == BootMethod.Grub)
        {
            partitions.Add(new PlannedPartition
            {
                Role = PartitionRole.BiosBoot,
                StartBytes = cursor,
                LengthBytes = PlanLimits.BiosBootBytes,
                GptType = GptTypes.BiosBoot,
            });
            cursor += PlanLimits.BiosBootBytes;
        }

        var persistenceWanted = PersistenceWanted(ctx, intent.WindowsToGo);
        var requiredMain = RequiredMain(ctx, intent.WindowsToGo, cursor);
        if (space.End - cursor < requiredMain)
        {
            throw ctx.DeviceTooSmall(ctx.DeviceBytes - (space.End - cursor) + requiredMain);
        }

        // Persistence is trimmed to fit later; it must not drag the budget below what the image needs.
        var budget = Math.Max(space.End - cursor - persistenceWanted, requiredMain);
        var choice = intent.WindowsToGo
            ? WindowsToGoFileSystem(ctx)
            : FileSystemRules.Choose(ctx, budget, ctx.Target.ClusterSizeBytes);
        var fileSystem = choice.FileSystem;

        var usesUefiNtfs = !intent.WindowsToGo && intent.Uefi && (fileSystem is FileSystemKind.Ntfs or FileSystemKind.ExFat)
            && ctx.Purpose is MediaPurpose.Windows or MediaPurpose.Linux;
        var tailStart = usesUefiNtfs ? PartitionAlignment.AlignDown(space.End - PlanLimits.UefiNtfsBytes, PlanLimits.Mib) : space.End;

        var available = tailStart - cursor;
        var baseCost = ctx.DeviceBytes - available;
        if (available < requiredMain)
        {
            throw ctx.DeviceTooSmall(baseCost + requiredMain + (persistenceWanted > 0 ? PlanLimits.MinPersistenceBytes : 0));
        }

        var persistence = GrantPersistence(ctx, persistenceWanted, available - requiredMain, baseCost + requiredMain);
        var persistenceStart = persistence > 0 ? PartitionAlignment.AlignDown(tailStart - persistence, PlanLimits.Mib) : tailStart;

        var mainLength = CapMain(ctx, fileSystem, intent.Legacy, cursor, persistenceStart - cursor, requiredMain);
        if (FileSystemRules.IsFat(fileSystem) && !FileSystemRules.Fits(fileSystem, mainLength, ss, ctx.Target.ClusterSizeBytes))
        {
            throw PlanContext.Unsupported(ErrorCode.FileSystemTooSmall, fileSystem, SizeText.Format(mainLength));
        }

        partitions.Add(MainPartition(ctx, intent, fileSystem, cursor, mainLength));
        if (persistence > 0)
        {
            partitions.Add(PersistencePartition(ctx, persistenceStart, persistence));
        }

        if (usesUefiNtfs)
        {
            partitions.Add(UefiNtfsPartition(tailStart));
            ctx.Warn(PlanWarningCodes.UefiNtfsCa2011);
        }

        AddFileSystemWarnings(ctx, fileSystem);
        return new FileLayout(partitions, usesUefiNtfs, choice.SplitWim, persistence > 0);
    }

    private static PlannedPartition MainPartition(PlanContext ctx, MediumIntent intent, FileSystemKind fileSystem, long start, long length)
    {
        var gpt = intent.Scheme == PartitionScheme.Gpt;
        var chsTypes = intent.Legacy || ctx.Purpose == MediaPurpose.Dos;

        return new PlannedPartition
        {
            Role = PartitionRole.Main,
            StartBytes = start,
            LengthBytes = length,
            FileSystem = fileSystem,
            Label = ctx.Target.Label ?? ctx.Image.VolumeLabel,
            Active = !gpt && intent.Bios && !intent.WindowsToGo,
            MbrType = FileSystemRules.MbrType(fileSystem, length, start + length, chsTypes, ctx.SectorSize),
            GptType = FileSystemRules.GptType(fileSystem),

            // gptmbr.bin boots the partition that carries the "legacy BIOS bootable" attribute.
            GptAttributes = gpt && intent.BiosMethod == BootMethod.SyslinuxMbr ? GptAttributes.LegacyBiosBootable : GptAttributes.None,
            ClusterSizeBytes = ctx.Target.ClusterSizeBytes,
        };
    }

    private static PlannedPartition UefiNtfsPartition(long start) => new()
    {
        Role = PartitionRole.UefiNtfs,
        StartBytes = start,
        LengthBytes = PlanLimits.UefiNtfsBytes,
        Label = "UEFI_NTFS",
        MbrType = MbrPartitionType.EfiSystem,

        // Windows setup cannot cope with a second ESP, so on GPT the helper is a hidden data partition.
        GptType = GptTypes.BasicData,
        GptAttributes = GptAttributes.NoDriveLetter,
    };

    private static PlannedPartition PersistencePartition(PlanContext ctx, long start, long length) => new()
    {
        Role = PartitionRole.Persistence,
        StartBytes = start,
        LengthBytes = length,
        FileSystem = FileSystemKind.Ext3,
        Label = LinuxFamilies.PersistenceLabel(ctx.Image.Family),
        MbrType = MbrPartitionType.Linux,
        GptType = GptTypes.LinuxData,
    };

    /// <summary>The range partitions may occupy: from the first allowed start to the last usable byte, aligned.</summary>
    private static (long Start, long End) FreeSpace(PlanContext ctx, MediumIntent intent)
    {
        var ss = ctx.SectorSize;
        var start = intent.Legacy
            ? PartitionAlignment.LegacyStartLba(ctx.Target.LegacyStart, ss) * ss
            : PlanLimits.Mib;

        if (intent.Legacy && !PartitionAlignment.IsAligned(start / ss, ss, Math.Max(ss, ctx.Device.PhysicalSectorSize)))
        {
            ctx.Warn(PlanWarningCodes.LegacyMisaligned);
        }

        if (intent.BiosMethod == BootMethod.Grub && intent.Scheme == PartitionScheme.Mbr && start < PartitionAlignment.BootloaderGapBytes)
        {
            start = PartitionAlignment.BootloaderGapBytes;
            ctx.Warn(PlanWarningCodes.GrubNeedsGap);
        }

        if (intent.Scheme == PartitionScheme.Gpt)
        {
            var tailSectors = 1 + (GptBuilder.EntryArrayBytes + ss - 1) / ss;
            return (start, PartitionAlignment.AlignDown(ctx.DeviceBytes - tailSectors * ss, PlanLimits.Mib));
        }

        var addressable = uint.MaxValue * (long)ss;
        var end = Math.Min(ctx.DeviceBytes, addressable);
        if (ctx.DeviceBytes > addressable)
        {
            ctx.Warn(PlanWarningCodes.MbrCapped, SizeText.Format(end), SizeText.Format(ctx.DeviceBytes));
        }

        return (start, intent.Legacy ? end : PartitionAlignment.AlignDown(end, PlanLimits.Mib));
    }

    private static long PersistenceWanted(PlanContext ctx, bool windowsToGo)
    {
        var megabytes = ctx.Target.PersistenceMegabytes;
        if (megabytes <= 0)
        {
            return 0;
        }

        if (ctx.Purpose != MediaPurpose.Linux || windowsToGo)
        {
            ctx.Warn(PlanWarningCodes.PersistenceUnsupported);
            return 0;
        }

        var bytes = megabytes * PlanLimits.Mib;
        return bytes >= PlanLimits.MinPersistenceBytes
            ? bytes
            : throw PlanContext.Unsupported(
                ErrorCode.PersistenceTooSmall, SizeText.Format(bytes), SizeText.Format(PlanLimits.MinPersistenceBytes));
    }

    /// <summary>How much of the wanted persistence fits next to the image; less than the minimum means the device is too small.</summary>
    private static long GrantPersistence(PlanContext ctx, long wanted, long room, long requiredWithoutPersistence)
    {
        if (wanted == 0)
        {
            return 0;
        }

        var granted = Math.Min(wanted, PartitionAlignment.AlignDown(room, PlanLimits.Mib));
        if (granted < PlanLimits.MinPersistenceBytes)
        {
            throw ctx.DeviceTooSmall(requiredWithoutPersistence + PlanLimits.MinPersistenceBytes);
        }

        if (granted < wanted)
        {
            ctx.Warn(PlanWarningCodes.PersistenceReduced, SizeText.Format(wanted), SizeText.Format(granted));
        }

        return granted;
    }

    private static long RequiredMain(PlanContext ctx, bool windowsToGo, long cursor)
    {
        var image = ctx.Image;
        var content = image.TotalBytes > 0 ? image.TotalBytes + PlanLimits.ExtractOverhead(image.TotalBytes) : 0;
        var required = windowsToGo ? Math.Max(PlanLimits.WindowsToGoMinBytes - cursor, content) : content;
        return PartitionAlignment.AlignUp(required, PlanLimits.Mib);
    }

    /// <summary>The Windows partition of a Windows To Go drive is always NTFS; anything else was asked for by mistake.</summary>
    private static FileSystemChoice WindowsToGoFileSystem(PlanContext ctx)
    {
        var requested = ctx.Target.FileSystem;
        return requested is FileSystemKind.Auto or FileSystemKind.Ntfs
            ? new FileSystemChoice(FileSystemKind.Ntfs, false)
            : throw PlanContext.Unsupported(ErrorCode.FileSystemUnsupported, requested, "WindowsToGo");
    }

    /// <summary>
    /// Limits the main partition to what its file system and, for old BIOSes, the 28-bit LBA range
    /// can address. Whatever is cut off stays unallocated and is reported.
    /// </summary>
    private static long CapMain(PlanContext ctx, FileSystemKind fileSystem, bool legacy, long start, long available, long required)
    {
        var dos = ctx.Purpose == MediaPurpose.Dos;
        var byFileSystem = FileSystemRules.MaxBytes(fileSystem, ctx.SectorSize, dos);
        var byLegacy = legacy ? PlanLimits.Lba28LimitBytes - start : long.MaxValue;
        var limit = Math.Min(byFileSystem, byLegacy);

        var align = legacy ? ctx.SectorSize : PlanLimits.Mib;
        var length = PartitionAlignment.AlignDown(Math.Min(available, limit), align);
        if (length < required)
        {
            throw limit < available
                ? PlanContext.Unsupported(ErrorCode.FileSystemTooSmall, fileSystem, SizeText.Format(length))
                : ctx.DeviceTooSmall(ctx.DeviceBytes - available + required);
        }

        if (length < available)
        {
            if (byLegacy < byFileSystem)
            {
                ctx.Warn(PlanWarningCodes.LegacyCapacityLimited, SizeText.Format(length), SizeText.Format(ctx.DeviceBytes));
            }
            else
            {
                ctx.Warn(PlanWarningCodes.CapacityNotUsed, SizeText.Format(length), SizeText.Format(available - length));
            }
        }

        return length;
    }

    /// <summary>ESP and, on GPT, the MSR in front of the Windows partition of a Windows To Go drive.</summary>
    private static long AddSystemPartitions(List<PlannedPartition> partitions, MediumIntent intent, long cursor)
    {
        var gpt = intent.Scheme == PartitionScheme.Gpt;
        partitions.Add(new PlannedPartition
        {
            Role = PartitionRole.Esp,
            StartBytes = cursor,
            LengthBytes = PlanLimits.EspBytes,
            FileSystem = FileSystemKind.Fat32,
            Label = "System",
            Active = !gpt,
            MbrType = intent.Uefi ? MbrPartitionType.EfiSystem : MbrPartitionType.Fat32Lba,
            GptType = GptTypes.EfiSystem,
        });
        cursor += PlanLimits.EspBytes;

        if (gpt)
        {
            partitions.Add(new PlannedPartition
            {
                Role = PartitionRole.Msr,
                StartBytes = cursor,
                LengthBytes = PlanLimits.MsrBytes,
                GptType = GptTypes.MicrosoftReserved,
            });
            cursor += PlanLimits.MsrBytes;
        }

        return cursor;
    }

    private static void AddFileSystemWarnings(PlanContext ctx, FileSystemKind fileSystem)
    {
        if (ctx.Purpose != MediaPurpose.Linux)
        {
            return;
        }

        if (fileSystem == FileSystemKind.Ntfs)
        {
            ctx.Warn(PlanWarningCodes.LinuxNtfsSupport);
        }

        if (fileSystem == FileSystemKind.ExFat && LinuxFamilies.UsesCasper(ctx.Image.Family))
        {
            ctx.Warn(PlanWarningCodes.ExFatCasper);
        }
    }
}
