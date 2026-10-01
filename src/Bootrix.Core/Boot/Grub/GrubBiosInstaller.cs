// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Bootrix.Core.Errors;
using Bootrix.Core.Model;

namespace Bootrix.Core.Boot.Grub;

/// <param name="Scheme">Partition table of the disk; selects the core image built for it.</param>
/// <param name="EmbedStartSector">Where the core image goes: sector 1 in the gap behind an MBR, the first sector of the BIOS boot partition on GPT.</param>
/// <param name="EmbedSectorLimit">How many sectors from <paramref name="EmbedStartSector"/> on are free for it.</param>
public sealed record GrubInstallRequest(PartitionScheme Scheme, long EmbedStartSector, long EmbedSectorLimit);

public sealed record GrubInstallResult(long CoreSector, int CoreSectors);

/// <summary>
/// Writes GRUB 2 for BIOS the way grub-bios-setup does when it embeds the core image: boot.img goes in front of the
/// partition table (the table and disk signature stay), the core image into free sectors, and the two are linked
/// by a sector number in boot.img and a block list at the end of the core image's first sector.
/// </summary>
public static class GrubBiosInstaller
{
    private const int SectorSize = 512;
    private const int BootCodeLength = 440;

    // Offsets in boot.img and in the first sector of core.img (include/grub/i386/pc/boot.h).
    private const int KernelSectorOffset = 0x5C;
    private const int DriveCheckOffset = 0x66;
    private const int BlockListOffset = SectorSize - 12;
    private const ushort KernelSegment = 0x820;

    private const string Component = "GRUB";

    /// <exception cref="BootrixException">The core image does not fit into the space that was set aside for it.</exception>
    public static GrubInstallResult Install(Stream disk, GrubBundle bundle, GrubInstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(request);

        var (boot, core, coreSectors) = Build(bundle, request);
        disk.Position = request.EmbedStartSector * SectorSize;
        disk.Write(core);

        // Only the boot code is replaced: bytes 440 and up hold the disk signature, the partition table and 0x55AA.
        var mbr = new byte[SectorSize];
        disk.Position = 0;
        disk.ReadExactly(mbr);
        boot.AsSpan(0, BootCodeLength).CopyTo(mbr);
        disk.Position = 0;
        disk.Write(mbr);
        disk.Flush();

        if (Verify(disk, bundle, request) is { } problem)
        {
            throw Failed("the installation does not verify: " + problem);
        }

        return new GrubInstallResult(request.EmbedStartSector, coreSectors);
    }

    /// <summary>Compares what is on the disk with what <see cref="Install"/> writes; null when they agree, otherwise what differs.</summary>
    public static string? Verify(Stream disk, GrubBundle bundle, GrubInstallRequest request)
    {
        ArgumentNullException.ThrowIfNull(disk);
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(request);

        var (boot, core, _) = Build(bundle, request);
        var onDisk = new byte[core.Length];
        disk.Position = request.EmbedStartSector * SectorSize;
        disk.ReadExactly(onDisk);
        if (!onDisk.AsSpan().SequenceEqual(core))
        {
            return "the core image on the disk differs from the one that was written";
        }

        var mbr = new byte[SectorSize];
        disk.Position = 0;
        disk.ReadExactly(mbr);
        return mbr.AsSpan(0, BootCodeLength).SequenceEqual(boot.AsSpan(0, BootCodeLength))
            ? null
            : "the boot code in the first sector differs from the one that was written";
    }

    private static (byte[] Boot, byte[] Core, int CoreSectors) Build(GrubBundle bundle, GrubInstallRequest request)
    {
        if (request.EmbedStartSector < 1)
        {
            throw Failed("the core image cannot be placed in sector 0");
        }

        var source = bundle.CoreFor(request.Scheme).Span;
        var coreSectors = (source.Length + SectorSize - 1) / SectorSize;
        if (coreSectors > request.EmbedSectorLimit)
        {
            throw Failed($"the core image needs {coreSectors} sectors, only {request.EmbedSectorLimit} are free");
        }

        // The image is padded to whole sectors so that what is read back can be compared exactly.
        var core = new byte[coreSectors * SectorSize];
        source.CopyTo(core);

        // The first sector of the core image loads the other sectors from the block list at its end.
        BinaryPrimitives.WriteUInt64LittleEndian(core.AsSpan(BlockListOffset), (ulong)request.EmbedStartSector + 1);
        BinaryPrimitives.WriteUInt16LittleEndian(core.AsSpan(BlockListOffset + 8), (ushort)(coreSectors - 1));
        BinaryPrimitives.WriteUInt16LittleEndian(core.AsSpan(BlockListOffset + 10), KernelSegment);

        var boot = bundle.BootImage.ToArray();
        BinaryPrimitives.WriteUInt64LittleEndian(boot.AsSpan(KernelSectorOffset), (ulong)request.EmbedStartSector);

        // For hard disks grub-bios-setup replaces the jump that trusts the BIOS drive number with two NOPs, so that a
        // BIOS handing over 0 or 1 for a stick it booted from 0x80 still ends up on the right drive.
        boot[DriveCheckOffset] = 0x90;
        boot[DriveCheckOffset + 1] = 0x90;
        return (boot, core, coreSectors);
    }

    private static BootrixException Failed(string detail) =>
        new(ErrorCode.BootloaderInstallFailed, detail) { Arguments = [Component, detail] };
}
