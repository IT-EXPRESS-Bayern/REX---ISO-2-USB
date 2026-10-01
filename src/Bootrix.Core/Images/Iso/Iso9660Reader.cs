// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;

namespace Bootrix.Core.Images.Iso;

/// <summary>What the volume descriptor sequence of an ISO 9660 / UDF bridge image says.</summary>
/// <param name="SystemId">System identifier of the primary descriptor (e.g. "LINUX" or "Win32").</param>
/// <param name="VolumeId">Volume label.</param>
/// <param name="VolumeSpaceBlocks">Number of logical blocks the volume occupies.</param>
/// <param name="BlockSize">Logical block size, almost always 2048.</param>
/// <param name="HasJoliet">A supplementary descriptor with a Joliet escape sequence exists.</param>
/// <param name="BootCatalogSector">El Torito boot catalog location, if a boot record names one.</param>
/// <param name="HasUdfRecognition">The sequence is followed by BEA01/NSR0x/TEA01, i.e. the image is a UDF bridge.</param>
public sealed record Iso9660Volume(
    string SystemId,
    string VolumeId,
    long VolumeSpaceBlocks,
    int BlockSize,
    bool HasJoliet,
    uint? BootCatalogSector,
    bool HasUdfRecognition)
{
    public long VolumeBytes => VolumeSpaceBlocks * BlockSize;
}

public static class Iso9660Reader
{
    private const int SectorSize = 2048;
    private const int FirstDescriptorSector = 16;
    private const int MaxDescriptors = 32;

    /// <summary>Walks the volume descriptors starting at sector 16; null when there is no primary volume descriptor.</summary>
    public static Iso9660Volume? Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var sector = new byte[SectorSize];
        string? systemId = null;
        string? volumeId = null;
        long blocks = 0;
        var blockSize = SectorSize;
        var joliet = false;
        uint? catalog = null;
        var udf = false;
        var terminated = false;

        for (var i = 0; i < MaxDescriptors; i++)
        {
            var offset = (FirstDescriptorSector + i) * (long)SectorSize;
            if (offset + SectorSize > stream.Length)
            {
                break;
            }

            stream.Position = offset;
            stream.ReadExactly(sector);

            if (sector.AsSpan(1, 5).SequenceEqual("CD001"u8) && !terminated)
            {
                switch (sector[0])
                {
                    case 0 when IsElToritoRecord(sector):
                        catalog = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(0x47));
                        break;
                    case 1 when systemId is null:
                        systemId = ReadText(sector.AsSpan(8, 32));
                        volumeId = ReadText(sector.AsSpan(40, 32));
                        blocks = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(80));
                        blockSize = BinaryPrimitives.ReadUInt16LittleEndian(sector.AsSpan(128));
                        break;
                    case 2 when HasJolietEscape(sector.AsSpan(88, 32)):
                        joliet = true;
                        break;
                    case 0xFF:
                        terminated = true;
                        break;
                }

                continue;
            }

            if (IsUdfRecognition(sector))
            {
                udf = true;
                continue;
            }

            break;
        }

        return systemId is null
            ? null
            : new Iso9660Volume(systemId, volumeId!, blocks, blockSize == 0 ? SectorSize : blockSize, joliet, catalog, udf);
    }

    private static bool IsElToritoRecord(ReadOnlySpan<byte> sector) =>
        sector[6] == 1 && sector.Slice(7, 23).SequenceEqual("EL TORITO SPECIFICATION"u8);

    /// <summary>Joliet is announced by "%/@", "%/C" or "%/E" (UCS-2 level 1 to 3) in the escape sequence field.</summary>
    private static bool HasJolietEscape(ReadOnlySpan<byte> escapes) =>
        escapes[0] == (byte)'%' && escapes[1] == (byte)'/' && escapes[2] is (byte)'@' or (byte)'C' or (byte)'E';

    private static bool IsUdfRecognition(ReadOnlySpan<byte> sector) =>
        sector[0] == 0 && (sector.Slice(1, 5).SequenceEqual("BEA01"u8) || sector.Slice(1, 5).SequenceEqual("NSR02"u8)
            || sector.Slice(1, 5).SequenceEqual("NSR03"u8) || sector.Slice(1, 5).SequenceEqual("TEA01"u8));

    private static string ReadText(ReadOnlySpan<byte> field) => Encoding.Latin1.GetString(field).TrimEnd(' ', '\0');
}
