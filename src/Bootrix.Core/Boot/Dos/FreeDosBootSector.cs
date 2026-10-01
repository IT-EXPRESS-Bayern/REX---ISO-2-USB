// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.FileSystems.Fat;

namespace Bootrix.Core.Boot.Dos;

/// <summary>
/// The FreeDOS boot sectors, assembled from the FreeDOS kernel sources (tools/bootcode). They only
/// bring the jump and the code; the BPB, the FAT32 FSInfo sector and the backup boot sector copy are
/// written by <see cref="FatFormatter"/> around them, so the geometry, the hidden sectors and the
/// drive number in the BPB are those of the plan.
/// </summary>
public static class FreeDosBootSector
{
    /// <summary>The loader for a volume of the given type; the FAT32 loader needs LBA addressing and a 386.</summary>
    public static byte[] CodeFor(FatType type) => DosAssets.FreeDos(type switch
    {
        FatType.Fat12 => "fat12com.bin",
        FatType.Fat16 => "fat16com.bin",
        _ => "fat32lba.bin",
    });

    /// <summary>
    /// Sets the boot code of <paramref name="options"/> to the loader that matches the type the volume will
    /// have. The type follows from the cluster count alone, so it is derived the way the formatter does.
    /// </summary>
    public static FatFormatOptions Apply(FatFormatOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.BytesPerSector != 512)
        {
            throw new BootrixException(ErrorCode.SectorSizeUnsupported, $"{options.BytesPerSector} bytes per sector")
            {
                Arguments = [options.BytesPerSector, "FreeDOS"],
            };
        }

        return options with { BootCode = CodeFor(FatGeometry.Compute(options).Type) };
    }
}
