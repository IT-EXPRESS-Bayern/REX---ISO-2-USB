// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images.Compression;
using Bootrix.Core.Images.Disk;
using Bootrix.Core.Images.Iso;

namespace Bootrix.Core.Images;

public sealed record ImageFileEntry(string Path, long Length, bool IsDirectory = false);

/// <summary>The outcome of <see cref="ImageInspector"/>: the profile the planner needs plus the details behind it.</summary>
public sealed record ImageInspection
{
    public required ImageProfile Profile { get; init; }

    public required ImageContainer Container { get; init; }

    public string? FileName { get; init; }

    /// <summary>Length of the file that was inspected, i.e. the compressed length for a compressed image.</summary>
    public long FileLength { get; init; }

    /// <summary>Length of the image after decompression; null when the container does not record it.</summary>
    public long? ImageLength { get; init; }

    /// <summary>For gzip only: the size modulo 2^32 from the trailer, usable as a hint.</summary>
    public long? ImageLengthHint { get; init; }

    public CompressionFormat Compression { get; init; }

    /// <summary>Name of the zip entry that was inspected.</summary>
    public string? ArchiveEntry { get; init; }

    public IReadOnlyList<ArchiveEntryInfo> ArchiveEntries { get; init; } = [];

    /// <summary>Only the beginning of a compressed image was decoded, so the file tree is missing or incomplete.</summary>
    public bool IsPartial { get; init; }

    /// <summary>First line of <c>.disk/info</c> or the product line of <c>.treeinfo</c>, e.g. "Ubuntu 24.04 LTS".</summary>
    public string? ReleaseInfo { get; init; }

    public Iso9660Volume? Volume { get; init; }

    public ElToritoCatalog? BootCatalog { get; init; }

    /// <summary>Paths inside the El Torito EFI image (or the first floppy image), if one could be read.</summary>
    public IReadOnlyList<string> BootImageFiles { get; init; } = [];

    public DiskLayout? Layout { get; init; }

    public WindowsImageInfo? Windows { get; init; }

    /// <summary>EFI architectures with a loader (BOOTX64.EFI and friends) in the file tree or the EFI image.</summary>
    public IReadOnlyList<WindowsArch> EfiArchitectures { get; init; } = [];

    public int FileCount { get; init; }

    /// <summary>Entries of the root directory.</summary>
    public IReadOnlyList<ImageFileEntry> RootEntries { get; init; } = [];

    public IReadOnlyList<ImageFileEntry> LargestFiles { get; init; } = [];

    /// <summary>A <c>.bmap</c> file next to the image, usable to skip unallocated blocks.</summary>
    public string? BmapPath { get; init; }

    public IReadOnlyList<ImageWarning> Warnings { get; init; } = [];

    /// <summary>The image is cut off (an error-level finding about missing data).</summary>
    public bool IsTruncated { get; init; }

    public bool HasErrors => Warnings.Any(warning => warning.Severity == WarningSeverity.Error);
}
