// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.FileSystems.Fat;
using Bootrix.Core.Writing.Linux;
using DiscUtils;
using DiscUtils.Fat;
using DiscUtils.Streams;

namespace Bootrix.Core.Writing.Windows;

/// <summary>
/// The contents of the small FAT partition that lets UEFI computers start from an NTFS or exFAT medium:
/// the UEFI:NTFS loader plus the file system drivers. Written as it is, byte for byte, because its files are
/// signed; the signature covers the files, not the FAT around them.
/// </summary>
public static class UefiNtfsImage
{
    /// <summary>Size of the partition the planner reserves, and of the shipped image.</summary>
    public const int PartitionBytes = UefiNtfsHelper.ImageBytes;

    /// <summary>
    /// The bytes for the start of the partition. The shipped image is a FAT volume with 512-byte sectors;
    /// firmware on a 4096-byte-sector device cannot read that, so there the same files are put into a new FAT
    /// volume with matching sectors.
    /// </summary>
    /// <exception cref="NotSupportedException">The sector size is neither 512 nor 4096.</exception>
    /// <exception cref="BootrixException">The files do not fit into the partition with this sector size.</exception>
    public static byte[] ForSectorSize(int sectorSize)
    {
        var shipped = UefiNtfsHelper.Image();
        return sectorSize switch
        {
            512 => shipped,
            4096 => Rebuild(shipped, sectorSize),
            _ => throw new NotSupportedException($"UEFI:NTFS has no image for {sectorSize}-byte sectors."),
        };
    }

    private static byte[] Rebuild(byte[] shipped, int sectorSize)
    {
        var image = new byte[PartitionBytes];
        using var target = new MemoryStream(image);

        string? label;
        using (var shippedStream = new MemoryStream(shipped, writable: false))
        using (var source = new FatFileSystem(shippedStream, Ownership.None))
        {
            label = source.VolumeLabel;
            FatFormatter.Format(target, new FatFormatOptions { TotalBytes = PartitionBytes, BytesPerSector = sectorSize, Label = label, AssumeZeroed = true });
            using var destination = new FatFileSystem(target, Ownership.None);
            try
            {
                Copy(source, destination, source.Root);
            }
            catch (IOException ex)
            {
                throw new BootrixException(ErrorCode.SectorSizeUnsupported, "the UEFI:NTFS files do not fit with this sector size", ex)
                {
                    Arguments = [sectorSize, "UEFI:NTFS"],
                };
            }
        }

        return image;
    }

    private static void Copy(DiscFileSystem source, DiscFileSystem destination, DiscDirectoryInfo directory)
    {
        foreach (var file in directory.GetFiles())
        {
            using var input = source.OpenFile(file.FullName, FileMode.Open, FileAccess.Read);
            using var output = destination.OpenFile(file.FullName, FileMode.Create, FileAccess.Write);
            input.CopyTo(output);
            destination.SetLastWriteTimeUtc(file.FullName, file.LastWriteTimeUtc);
        }

        foreach (var child in directory.GetDirectories())
        {
            destination.CreateDirectory(child.FullName);
            Copy(source, destination, child);
        }
    }
}
