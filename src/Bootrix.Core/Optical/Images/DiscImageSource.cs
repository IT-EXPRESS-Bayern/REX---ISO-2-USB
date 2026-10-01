// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Optical.Images;

public enum DiscImageKind
{
    Unknown,

    /// <summary>2048-byte sectors with an ISO 9660 or UDF structure: .iso, and Apple's .cdr, which is the same thing.</summary>
    Iso,

    /// <summary>A .cue sheet and the raw .bin file(s) it describes.</summary>
    BinCue,

    /// <summary>Apple disk image; decoded by the image reader, which hands the result in as a stream.</summary>
    Dmg,

    /// <summary>A disk image with a partition table and no optical structure; belongs on a USB stick, not on a disc.</summary>
    DiskImage,

    /// <summary>Raw 2352-byte sectors without a cue sheet; the track layout is unknown.</summary>
    RawSectors,

    /// <summary>CloneCD (.img with .ccd and .sub) or Nero (.nrg) or Alcohol (.mdf/.mds): recognised, but not written.</summary>
    CloneCd,
    Nero,
    Alcohol,
}

/// <summary>
/// An image ready to burn, as a source of 2048-byte data sectors. <see cref="OpenStream"/> is a factory because
/// every drive in a multi-drive burn reads the image on its own, and because some images (DMG, BIN/CUE) are
/// decoded on the fly.
/// </summary>
/// <param name="LengthBytes">Length of the user data; it need not be a multiple of the sector size, the burn pads it.</param>
public sealed record DiscImageSource(string DisplayName, DiscImageKind Kind, long LengthBytes, Func<Stream> OpenStream)
{
    public long SectorCount => SectorMath.SectorsFor(LengthBytes);

    public long PaddedLengthBytes => SectorMath.RoundUpToSector(LengthBytes);

    /// <summary>The image as a stream whose length is a whole number of sectors, which is what IMAPI insists on.</summary>
    public Stream OpenPadded() => new SectorPaddedStream(OpenStream(), LengthBytes);
}
