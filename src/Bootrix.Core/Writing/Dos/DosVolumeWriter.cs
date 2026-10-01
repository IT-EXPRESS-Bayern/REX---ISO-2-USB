// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Bootrix.Core.Boot.Dos;
using Bootrix.Core.Errors;
using Bootrix.Core.Planning;
using DiscFatFileSystem = DiscUtils.Fat.FatFileSystem;

namespace Bootrix.Core.Writing.Dos;

/// <summary>
/// Copies the files of a DOS system disk into a FAT volume that is not mounted anywhere: a diskette, a
/// superfloppy stick, a partition inside an image. The files are written one after another into the
/// empty volume, so each one occupies consecutive clusters and the system files come first, which is
/// what the boot sectors of MS-DOS and PC-DOS rely on.
/// </summary>
public static class DosVolumeWriter
{
    private const int FsInfoFreeCountOffset = 0x1E8;

    /// <param name="volume">The volume, starting at offset 0 of the stream.</param>
    public static void Write(Stream volume, IReadOnlyList<DosFile> files, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(files);

        long freeClusters;
        using (var fat = new DiscFatFileSystem(volume))
        {
            EnsureRoom(fat, files);

            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (file.Folder.Length > 0 && folders.Add(file.Folder))
                {
                    fat.CreateDirectory(file.Folder);
                }

                using (var target = fat.OpenFile(file.Path, FileMode.Create, FileAccess.Write))
                {
                    target.Write(file.Content);
                }

                fat.SetAttributes(file.Path, file.Attributes);
            }

            freeClusters = fat.AvailableSpace / ((long)fat.SectorsPerCluster * fat.SectorSize);
        }

        UpdateFsInfo(volume, (uint)freeClusters);
        volume.Flush();
    }

    /// <summary>
    /// DiscUtils leaves the FAT32 FSInfo sector as the formatter wrote it, with the free count of an empty volume.
    /// Drivers believe that number and fsck reports it, so it is brought up to date in the sector and in its backup copy.
    /// The hint for the next free cluster stays: it only says where to start looking.
    /// </summary>
    private static void UpdateFsInfo(Stream volume, uint freeClusters)
    {
        const int fat32TypeOffset = 0x52;
        var boot = new byte[512];
        volume.Position = 0;
        volume.ReadExactly(boot);
        if (!boot.AsSpan(fat32TypeOffset, 5).SequenceEqual("FAT32"u8))
        {
            return;
        }

        var bytesPerSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(0x0B));
        var fsInfoSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(0x30));
        var backupSector = BinaryPrimitives.ReadUInt16LittleEndian(boot.AsSpan(0x32));

        int[] sectors = backupSector is 0 or 0xFFFF ? [fsInfoSector] : [fsInfoSector, backupSector + fsInfoSector];
        var info = new byte[bytesPerSector];
        foreach (var sector in sectors)
        {
            volume.Position = (long)sector * bytesPerSector;
            volume.ReadExactly(info);
            BinaryPrimitives.WriteUInt32LittleEndian(info.AsSpan(FsInfoFreeCountOffset), freeClusters);
            volume.Position = (long)sector * bytesPerSector;
            volume.Write(info);
        }
    }

    private static void EnsureRoom(DiscFatFileSystem fat, IReadOnlyList<DosFile> files)
    {
        var clusterBytes = (long)fat.SectorsPerCluster * fat.SectorSize;
        var required = files.Sum(file => (file.Content.Length + clusterBytes - 1) / clusterBytes * clusterBytes);
        required += files.Select(file => file.Folder).Where(folder => folder.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Count() * clusterBytes;
        if (required > fat.AvailableSpace)
        {
            throw new BootrixException(ErrorCode.DeviceTooSmall, $"the system files need {required} bytes, the volume has {fat.AvailableSpace} free")
            {
                Arguments = [SizeText.Format(required), SizeText.Format(fat.AvailableSpace)],
            };
        }
    }
}
