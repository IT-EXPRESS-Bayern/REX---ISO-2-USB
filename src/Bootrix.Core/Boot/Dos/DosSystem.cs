// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.FileSystems.Fat;

namespace Bootrix.Core.Boot.Dos;

/// <summary>
/// Everything a DOS system disk consists of apart from the partition table: the files to copy and the
/// adjustment of the formatter so that the volume carries the boot code of that DOS.
/// </summary>
public sealed class DosSystem
{
    private readonly Func<FatFormatOptions, long, FatFormatOptions> _customize;

    internal DosSystem(DosFlavor flavor, IReadOnlyList<DosFile> files, Func<FatFormatOptions, long, FatFormatOptions> customize)
    {
        Flavor = flavor;
        Files = files;
        _customize = customize;
    }

    public DosFlavor Flavor { get; }

    /// <summary>In the order they must be written; the DOS loaders expect the system files first.</summary>
    public IReadOnlyList<DosFile> Files { get; }

    public long TotalBytes => Files.Sum(file => (long)file.Content.Length);

    /// <summary>
    /// Adds the boot code of this DOS to the options of a FAT volume, and with it whatever its loader
    /// needs from the BPB. <paramref name="diskSectors"/> is the size of the whole disk, which decides
    /// the geometry old BIOSes assume.
    /// </summary>
    public FatFormatOptions Customize(FatFormatOptions options, long diskSectors)
    {
        ArgumentNullException.ThrowIfNull(options);
        return _customize(options, diskSectors);
    }

    /// <summary>A copy that has <paramref name="file"/> in place of the file with the same path, or after the others when there is none.</summary>
    public DosSystem WithFile(DosFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var files = Files.ToList();
        var index = files.FindIndex(existing => string.Equals(existing.Path, file.Path, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            files[index] = file;
        }
        else
        {
            files.Add(file);
        }

        return new DosSystem(Flavor, files, _customize);
    }
}
