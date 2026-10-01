// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Bootrix.Core.Errors;

namespace Bootrix.Core.Optical.Images;

/// <summary>
/// Works out what a file is that someone wants to burn. The extension alone is not enough: .img can be
/// an ISO, a CloneCD image or a raw disk, and .bin is raw sectors that need their cue sheet.
/// </summary>
public static class DiscImageDetector
{
    private const long VolumeDescriptorStart = 16L * SectorMath.SectorSize;
    private const int VolumeDescriptorScan = 12;

    private static ReadOnlySpan<byte> Iso9660Id => "CD001"u8;

    private static ReadOnlySpan<byte> UdfRecognitionStart => "BEA01"u8;

    public static DiscImageKind Detect(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        switch (extension)
        {
            case ".cue":
                return DiscImageKind.BinCue;
            case ".dmg":
                return DiscImageKind.Dmg;
            case ".nrg":
                return DiscImageKind.Nero;
            case ".mdf" or ".mds":
                return DiscImageKind.Alcohol;
            case ".ccd" or ".sub":
                return DiscImageKind.CloneCd;
            case ".img" when File.Exists(Path.ChangeExtension(path, ".ccd")):
                return DiscImageKind.CloneCd;
            case ".bin" when File.Exists(Path.ChangeExtension(path, ".cue")):
                return DiscImageKind.BinCue;
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Detect(stream, extension);
    }

    /// <summary>Looks at the content only, for images that arrive as a stream. <paramref name="extension"/> may be empty.</summary>
    public static DiscImageKind Detect(Stream stream, string extension)
    {
        if (HasUdifTrailer(stream))
        {
            return DiscImageKind.Dmg;
        }

        if (HasNeroFooter(stream))
        {
            return DiscImageKind.Nero;
        }

        var head = ReadAt(stream, 0, VolumeDescriptorScan * SectorMath.SectorSize + VolumeDescriptorStart);
        if (head.Length >= 16 && head.AsSpan(0, 16).SequenceEqual("MEDIA DESCRIPTOR"u8))
        {
            return DiscImageKind.Alcohol;
        }

        if (HasVolumeDescriptors(head))
        {
            return DiscImageKind.Iso;
        }

        // Apple's CD/DVD masters carry HFS or HFS+ instead of ISO 9660, and burning them byte for byte is the point.
        if (string.Equals(extension, ".cdr", StringComparison.OrdinalIgnoreCase) || HasHfsVolumeHeader(head))
        {
            return DiscImageKind.Iso;
        }

        if (head.Length >= 12 && head.AsSpan(0, 12).SequenceEqual(new byte[] { 0, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0 }))
        {
            return DiscImageKind.RawSectors;
        }

        return head.Length >= 512 && head[510] == 0x55 && head[511] == 0xAA ? DiscImageKind.DiskImage : DiscImageKind.Unknown;
    }

    /// <summary>Opens an image for burning, or explains why it cannot be burned.</summary>
    public static DiscImageSource Open(string path)
    {
        var kind = Detect(path);
        switch (kind)
        {
            case DiscImageKind.Iso:
                var length = new FileInfo(path).Length;
                return new DiscImageSource(
                    Path.GetFileName(path),
                    kind,
                    length,
                    () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan));
            case DiscImageKind.BinCue:
                var cue = string.Equals(Path.GetExtension(path), ".cue", StringComparison.OrdinalIgnoreCase) ? path : Path.ChangeExtension(path, ".cue");
                return CueBurnPlanner.FromFile(cue);
            default:
                throw new BootrixException(ErrorCode.ImageUnsupported, $"{kind}: {path}") { Arguments = [Describe(kind)] };
        }
    }

    private static string Describe(DiscImageKind kind) => kind switch
    {
        DiscImageKind.Dmg => "DMG images are decoded by the image reader and burned from the decoded stream",
        DiscImageKind.DiskImage => "this is a disk image with a partition table; write it to a USB stick instead of burning it",
        DiscImageKind.RawSectors => "raw 2352-byte sectors need a cue sheet to tell the tracks apart",
        DiscImageKind.CloneCd => "CloneCD images are not supported; convert the image to ISO",
        DiscImageKind.Nero => "Nero (.nrg) images are not supported; convert the image to ISO",
        DiscImageKind.Alcohol => "Alcohol (.mdf/.mds) images are not supported; convert the image to ISO",
        _ => "the file has no ISO 9660 or UDF structure",
    };

    private static bool HasVolumeDescriptors(byte[] head)
    {
        for (var i = 0; i < VolumeDescriptorScan; i++)
        {
            var offset = (int)VolumeDescriptorStart + i * SectorMath.SectorSize;
            if (head.Length < offset + 6)
            {
                return false;
            }

            var id = head.AsSpan(offset + 1, 5);
            if (id.SequenceEqual(Iso9660Id) || id.SequenceEqual(UdfRecognitionStart))
            {
                return true;
            }
        }

        return false;
    }

    // HFS+ volume header at byte 1024 starts with "H+" (HFS+) or "HX" (HFSX); classic HFS has "BD" in its master directory block.
    private static bool HasHfsVolumeHeader(byte[] head) =>
        head.Length >= 1026
        && ((head[1024] == (byte)'H' && head[1025] is (byte)'+' or (byte)'X') || (head[1024] == (byte)'B' && head[1025] == (byte)'D'));

    private static bool HasUdifTrailer(Stream stream)
    {
        if (stream.Length < 512)
        {
            return false;
        }

        var trailer = ReadAt(stream, stream.Length - 512, 4);
        return trailer.AsSpan().SequenceEqual("koly"u8);
    }

    // The Nero footer is 8 bytes ("NERO" and a 32-bit offset) in old files and 12 bytes ("NER5" and a 64-bit offset) in new ones.
    private static bool HasNeroFooter(Stream stream)
    {
        if (stream.Length < 12)
        {
            return false;
        }

        var footer = ReadAt(stream, stream.Length - 12, 12);
        return footer.AsSpan(0, 4).SequenceEqual("NER5"u8) || footer.AsSpan(4, 4).SequenceEqual("NERO"u8);
    }

    private static byte[] ReadAt(Stream stream, long offset, long count)
    {
        if (offset >= stream.Length)
        {
            return [];
        }

        var buffer = new byte[Math.Min(count, stream.Length - offset)];
        stream.Position = offset;
        stream.ReadExactly(buffer);
        return buffer;
    }
}
