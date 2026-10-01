// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Bootrix.Core.Errors;
using Bootrix.Core.Images.Udif;

namespace Bootrix.Core.Images.Apple;

internal enum ContainerSignature
{
    None,
    Udif,
    SparseImage,
    EncryptedV1,
    EncryptedV2,
    AppleEncryptedArchive,
    DiskCopy,
    Ndif,
}

/// <summary>
/// Recognises Apple image containers from their signatures and turns the ones that cannot be
/// read into specific errors instead of letting them fall through as "raw" data.
/// </summary>
internal static class ContainerSniffer
{
    private const int TailWindow = 4096;

    public static ContainerSignature Detect(RandomAccessSource source)
    {
        Span<byte> head = stackalloc byte[512];
        source.ReadPadded(0, head);

        if (head.StartsWith("encrcdsa"u8))
        {
            return ContainerSignature.EncryptedV2;
        }

        if (head.StartsWith("AEA1"u8))
        {
            return ContainerSignature.AppleEncryptedArchive;
        }

        if (head.StartsWith("sprs"u8))
        {
            return ContainerSignature.SparseImage;
        }

        if (UdifTrailer.Find(source) is not null)
        {
            return ContainerSignature.Udif;
        }

        if (HasEncryptedV1Header(source))
        {
            return ContainerSignature.EncryptedV1;
        }

        if (LooksLikeDiskCopy(head, source.Length))
        {
            return ContainerSignature.DiskCopy;
        }

        return IsNdifMacBinary(head) ? ContainerSignature.Ndif : ContainerSignature.None;
    }

    /// <summary>Throws the specific error for containers that are recognised but not supported.</summary>
    public static void ThrowIfUnsupported(ContainerSignature signature)
    {
        switch (signature)
        {
            case ContainerSignature.EncryptedV1:
            case ContainerSignature.EncryptedV2:
                throw new BootrixException(ErrorCode.ImageEncrypted, "encrypted Apple disk image (encrcdsa/cdsaencr)");
            case ContainerSignature.AppleEncryptedArchive:
                throw new BootrixException(ErrorCode.ImageAppleArchive, "Apple Encrypted Archive (AEA1)");
            case ContainerSignature.DiskCopy:
                throw LegacyFormat("Disk Copy 4.2");
            case ContainerSignature.Ndif:
                throw LegacyFormat("NDIF");
        }
    }

    private static BootrixException LegacyFormat(string name) =>
        new(ErrorCode.ImageLegacyFormat, name) { Arguments = [name] };

    // The version 1 header starts with "cdsaencr" and sits at the end of the file.
    private static bool HasEncryptedV1Header(RandomAccessSource source)
    {
        var length = (int)Math.Min(source.Length, TailWindow);
        if (length < 8)
        {
            return false;
        }

        Span<byte> tail = stackalloc byte[TailWindow];
        tail = tail[..length];
        source.ReadExactlyAt(source.Length - length, tail);
        return tail.IndexOf("cdsaencr"u8) >= 0;
    }

    // Disk Copy 4.2: Pascal name, data size at 0x40, magic 0x0100 at 0x52, data from 0x54.
    private static bool LooksLikeDiskCopy(ReadOnlySpan<byte> head, long fileLength)
    {
        if (head[0] > 63 || BinaryPrimitives.ReadUInt16BigEndian(head[0x52..]) != 0x0100)
        {
            return false;
        }

        var dataSize = BinaryPrimitives.ReadUInt32BigEndian(head[0x40..]);
        return dataSize != 0 && dataSize % 512 == 0 && 0x54 + (long)dataSize <= fileLength;
    }

    // NDIF images travel as MacBinary files of type 'rohd'/'devc'/'dImg' or with the Disk Copy creator code.
    private static bool IsNdifMacBinary(ReadOnlySpan<byte> head)
    {
        if (head[0] != 0 || head[1] is 0 or > 63 || head[74] != 0 || head[82] != 0)
        {
            return false;
        }

        var fileType = head.Slice(65, 4);
        var creator = head.Slice(69, 4);
        return creator.SequenceEqual("ddsk"u8)
            || fileType.SequenceEqual("rohd"u8)
            || fileType.SequenceEqual("devc"u8)
            || fileType.SequenceEqual("dImg"u8);
    }
}
