// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images.Udif;
using Microsoft.Extensions.Logging;

namespace Bootrix.Core.Images.Apple;

/// <summary>
/// The decoded content of an Apple image as the source for a raw write to a USB stick, disk or disc:
/// a read-only seekable stream whose length is padded with zeros to a whole number of sectors.
/// Decoding of compressed formats happens while the stream is read.
/// </summary>
public sealed class AppleImageSource : IDisposable
{
    private readonly OpenedImage _image;

    private AppleImageSource(OpenedImage image, Stream stream, long contentLength, int alignment)
    {
        _image = image;
        Stream = stream;
        ContentLength = contentLength;
        Alignment = alignment;
    }

    /// <summary>The padded volume; <see cref="System.IO.Stream.Length"/> is a multiple of <see cref="Alignment"/>.</summary>
    public Stream Stream { get; }

    /// <summary>Length of the padded stream.</summary>
    public long Length => Stream.Length;

    /// <summary>Size of the volume inside the image, before padding.</summary>
    public long ContentLength { get; }

    /// <summary>Sector size the length was rounded up to: 512 for disks, 2048 for optical media.</summary>
    public int Alignment { get; }

    public AppleImageContainer Container => _image.Container;

    /// <summary>Details of the UDIF image, or null for other containers.</summary>
    public DmgInfo? Dmg => _image.Dmg;

    /// <summary>
    /// Opens a .dmg, .sparseimage, .sparsebundle directory or raw image (.cdr, .iso, .toast, .img).
    /// Encrypted, segmented and Mac OS 9 era images are rejected with a <see cref="Errors.BootrixException"/>.
    /// </summary>
    /// <param name="alignment">512 for disks and USB sticks, 2048 for CD/DVD/BD, 4096 for 4Kn media.</param>
    public static AppleImageSource OpenForRawWrite(string path, int alignment = 512, DmgReaderOptions? options = null, ILogger? logger = null)
    {
        if (alignment is not (512 or 2048 or 4096))
        {
            throw new ArgumentOutOfRangeException(nameof(alignment), alignment, "The alignment must be 512, 2048 or 4096.");
        }

        var image = AppleImageOpener.Open(path, options, logger);
        try
        {
            var length = image.Volume.Length;
            var padded = (length + alignment - 1) / alignment * alignment;
            Stream stream = padded == length ? image.Volume : new PaddedStream(image.Volume, padded);
            return new AppleImageSource(image, stream, length, alignment);
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Stream.Dispose();
        _image.Dispose();
    }
}
