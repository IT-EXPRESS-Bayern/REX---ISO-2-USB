// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace Bootrix.Core.Optical.Reading;

internal static class Iso9660Probe
{
    /// <summary>The primary volume descriptor is always sector 16; sectors 0 to 15 are the system area.</summary>
    public const int PrimaryDescriptorSector = 16;

    private static ReadOnlySpan<byte> Identifier => "CD001"u8;

    /// <summary>Volume size in sectors from the primary volume descriptor, or null when the disc has no ISO 9660 structure.</summary>
    public static long? ReadVolumeSectors(ISectorReader reader)
    {
        var buffer = new byte[SectorMath.SectorSize];
        var result = reader.Read(PrimaryDescriptorSector, 1, buffer);
        return result.SectorsRead == 1 ? ParseVolumeSectors(buffer) : null;
    }

    public static long? ParseVolumeSectors(ReadOnlySpan<byte> sector)
    {
        if (sector.Length < 88 || sector[0] != 1 || !sector.Slice(1, 5).SequenceEqual(Identifier) || sector[6] != 1)
        {
            return null;
        }

        // Volume space size is stored in both byte orders; the little-endian copy is read and the big-endian one checked,
        // since a descriptor where they disagree was not written by a tool that knows what it does.
        var little = BinaryPrimitives.ReadUInt32LittleEndian(sector[80..]);
        var big = BinaryPrimitives.ReadUInt32BigEndian(sector[84..]);
        return little == big && little > PrimaryDescriptorSector ? little : null;
    }
}
