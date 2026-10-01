// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images.Apple;
using Bootrix.Core.Images.Compression;

namespace Bootrix.Core.Writing.Raw;

public enum ImageSourceKind
{
    /// <summary>The file itself is the image.</summary>
    File,

    /// <summary>gzip, bzip2, xz, zstd, LZMA, compress or zip: the stream is decoded while it is read.</summary>
    Compressed,

    /// <summary>UDIF (.dmg), sparse image or sparse bundle: the stream is the volume inside the container.</summary>
    AppleContainer,
}

/// <summary>What the writer does with a block map (.bmap) that lies next to the image.</summary>
public enum BlockMapUse
{
    /// <summary>Write only the mapped blocks.</summary>
    Auto,

    /// <summary>Ignore the map and write the whole image.</summary>
    Off,

    /// <summary>Use the map to check the image, but write everything with zeros in the gaps, so the target does not keep old data there.</summary>
    FillGaps,
}

public sealed record ImageSourceOptions
{
    /// <summary>Entry of a zip archive that holds the image; by default the archive's image is chosen.</summary>
    public string? ArchiveEntry { get; init; }

    public BlockMapUse BlockMap { get; init; } = BlockMapUse.Auto;

    /// <summary>
    /// A sparse bundle is a folder whose band files are opened while reading, long after the folder was opened. A
    /// process that opens images on behalf of a less privileged user must not allow that.
    /// </summary>
    public bool AllowSparseBundle { get; init; } = true;

    /// <summary>Apple volumes are padded with zeros to a multiple of this; 512 for disks, 4096 for 4Kn media.</summary>
    public int SectorSize { get; init; } = 512;
}

/// <summary>An image opened for sequential reading, with whatever is known about its decoded size and its block map.</summary>
public sealed class ImageSource(Stream stream, long? length, ImageSourceKind kind) : IAsyncDisposable, IDisposable
{
    public Stream Stream { get; } = stream;

    /// <summary>Exact size of the decoded image; null for gzip, bzip2 and other formats that do not record it.</summary>
    public long? Length { get; } = length;

    public ImageSourceKind Kind { get; } = kind;

    public CompressionFormat Compression { get; init; }

    /// <summary>The zip entry that is being read.</summary>
    public string? ArchiveEntry { get; init; }

    public AppleImageContainer? AppleContainer { get; init; }

    /// <summary>The block map to write by; null when there is none, it was switched off or it did not fit the image.</summary>
    public BlockMap? BlockMap { get; init; }

    /// <summary>Why a block map next to the image was not used, for the log.</summary>
    public string? BlockMapSkipped { get; init; }

    public bool FillBlockMapGaps { get; init; }

    /// <summary>What has to be disposed together with the stream (the Apple reader owns more than the stream it hands out).</summary>
    internal IDisposable? Owner { get; init; }

    public void Dispose()
    {
        Stream.Dispose();
        Owner?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await Stream.DisposeAsync().ConfigureAwait(false);
        Owner?.Dispose();
    }
}
