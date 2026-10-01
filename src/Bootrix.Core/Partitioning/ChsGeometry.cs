// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Partitioning;

/// <summary>
/// The logical geometry a BIOS presents for a disk. Modern disks are addressed by LBA and the
/// numbers are fiction, but old boot code and DOS still read CHS values, and the fiction has to
/// be self-consistent between the MBR and the boot sector's BPB.
/// </summary>
public readonly record struct ChsGeometry
{
    public ChsGeometry(int heads, int sectorsPerTrack)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(heads, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(heads, 256);
        ArgumentOutOfRangeException.ThrowIfLessThan(sectorsPerTrack, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sectorsPerTrack, ChsAddress.MaxSector);
        Heads = heads;
        SectorsPerTrack = sectorsPerTrack;
    }

    /// <summary>The translation every BIOS and Windows use for disks above 8 GiB: 255 heads, 63 sectors.</summary>
    public static ChsGeometry Translated => new(255, 63);

    public int Heads { get; }

    public int SectorsPerTrack { get; }

    public long SectorsPerCylinder => (long)Heads * SectorsPerTrack;

    /// <summary>Sectors reachable through CHS at all: cylinders 0 to 1023.</summary>
    public long AddressableSectors => (ChsAddress.MaxCylinder + 1L) * SectorsPerCylinder;

    /// <summary>
    /// The head count an old BIOS picked for a disk of the given size: doubling from 16 heads
    /// until 1024 cylinders cover the disk (504 MiB, 1008 MiB, 2 GiB, 4 GiB), then 255.
    /// </summary>
    public static ChsGeometry ForCapacity(long totalSectors)
    {
        foreach (var heads in (ReadOnlySpan<int>)[16, 32, 64, 128])
        {
            if (totalSectors <= (ChsAddress.MaxCylinder + 1L) * heads * ChsAddress.MaxSector)
            {
                return new ChsGeometry(heads, ChsAddress.MaxSector);
            }
        }

        return Translated;
    }

    /// <summary>Addresses beyond cylinder 1023 come out as <see cref="ChsAddress.Unrepresentable"/>.</summary>
    public ChsAddress FromLba(long lba)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lba);
        if (SectorsPerTrack == 0)
        {
            throw new InvalidOperationException("The geometry was not initialized; use ChsGeometry.Translated or a constructor.");
        }

        if (lba >= AddressableSectors)
        {
            return ChsAddress.Unrepresentable;
        }

        var cylinder = (int)(lba / SectorsPerCylinder);
        var head = (int)(lba / SectorsPerTrack % Heads);
        var sector = (int)(lba % SectorsPerTrack) + 1;
        return new ChsAddress(cylinder, head, sector);
    }

    public long ToLba(ChsAddress address) =>
        ((address.Cylinder * (long)Heads) + address.Head) * SectorsPerTrack + address.Sector - 1;
}
