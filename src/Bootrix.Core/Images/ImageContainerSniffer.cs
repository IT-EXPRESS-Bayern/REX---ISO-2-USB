// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Bootrix.Core.Images.Disk;
using Bootrix.Core.Images.Iso;
using Bootrix.Core.Images.Wim;
using DiscUtils.Udf;

namespace Bootrix.Core.Images;

/// <summary>Decides what kind of image a (decompressed) stream is from its first 64 KiB and its last 512 bytes.</summary>
internal static class ImageContainerSniffer
{
    public const int HeadLength = 64 * 1024;
    private const int FooterLength = 512;

    public static ImageContainer Detect(Stream stream)
    {
        var length = stream.Length;
        var head = ReadAt(stream, 0, (int)Math.Min(HeadLength, length));
        var tail = length >= FooterLength ? ReadAt(stream, length - FooterLength, FooterLength) : [];

        if (WimHeader.HasSignature(head))
        {
            return ImageContainer.Wim;
        }

        if (head.AsSpan().StartsWith("vhdxfile"u8))
        {
            return ImageContainer.Vhdx;
        }

        if (IsVhdFooter(tail) || IsVhdFooter(head))
        {
            return ImageContainer.Vhd;
        }

        if (head.Length >= 16 && head.AsSpan(4, 12).SequenceEqual("SignedImage "u8))
        {
            return ImageContainer.Ffu;
        }

        if (IsUdifTrailer(tail) || head.AsSpan().StartsWith("sprs"u8))
        {
            return ImageContainer.AppleImage;
        }

        var iso = Iso9660Reader.Read(stream);
        if (iso is not null)
        {
            return iso.HasUdfRecognition ? ImageContainer.IsoUdfBridge : ImageContainer.Iso9660;
        }

        if (UdfReader.Detect(stream))
        {
            return ImageContainer.Udf;
        }

        if (FatBootSector.LooksLikeFatBootSector(head))
        {
            return ImageContainer.FatVolume;
        }

        var layout = DiskLayoutReader.Read(stream);
        return layout.HasPartitionTable || layout.HasApm || (layout.HasMbrSignature && layout.HasBootCode)
            ? ImageContainer.RawDisk
            : ImageContainer.Unknown;
    }

    /// <summary>The "conectix" cookie starts the 512-byte footer of every VHD and its copy at the start of dynamic ones.</summary>
    public static bool IsVhdFooter(ReadOnlySpan<byte> data) =>
        data.Length >= FooterLength && data.StartsWith("conectix"u8) && BinaryPrimitives.ReadUInt32BigEndian(data[12..]) == 0x00010000;

    /// <summary>Fixed VHDs are the raw disk followed by the footer; dynamic ones need a VHD reader.</summary>
    public static bool IsFixedVhd(ReadOnlySpan<byte> footer) =>
        IsVhdFooter(footer) && BinaryPrimitives.ReadUInt32BigEndian(footer[60..]) == 2;

    /// <summary>"koly" starts the 512-byte trailer of a UDIF disk image (.dmg).</summary>
    private static bool IsUdifTrailer(ReadOnlySpan<byte> tail) => tail.Length >= FooterLength && tail.StartsWith("koly"u8);

    public static byte[] ReadAt(Stream stream, long offset, int count)
    {
        var buffer = new byte[count];
        stream.Position = offset;
        var read = stream.ReadAtLeast(buffer, count, throwOnEndOfStream: false);
        return read == count ? buffer : buffer[..read];
    }
}
