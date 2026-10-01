// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images.Disk;
using DiscUtils.Streams;

namespace Bootrix.Core.Images.Iso;

/// <summary>
/// Opens the file system of an El Torito boot image: the FAT image behind an EFI entry, or the floppy or
/// hard disk image of an emulating entry. DOS-based tool discs and EFI-only loaders show up only here.
/// </summary>
internal static class BootImageReader
{
    public static ImageFileSystem? Open(Stream iso, ElToritoEntry entry, int entryLimit, CancellationToken cancellationToken)
    {
        var length = ElToritoParser.ResolveImageLength(iso, entry);
        if (length < 512 || entry.ImageOffset + 512 > iso.Length)
        {
            return null;
        }

        var image = new SubStream(iso, Ownership.None, entry.ImageOffset, length);
        var volume = ImageFileSystem.OpenFatRegion(image, 0, length, entryLimit, cancellationToken);
        if (volume is not null)
        {
            return volume;
        }

        // Hard disk emulation, and some EFI images, carry a partition table in front of the FAT volume.
        var volumes = PartitionFileSystems.Open(image, DiskLayoutReader.Read(image), entryLimit, cancellationToken);
        foreach (var other in volumes.Skip(1))
        {
            other.Dispose();
        }

        return volumes.FirstOrDefault();
    }
}
