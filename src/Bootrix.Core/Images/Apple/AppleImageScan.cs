// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace Bootrix.Core.Images.Apple;

internal readonly record struct MbrEntry(byte Type, long StartLba, long SectorCount);

/// <summary>The raw findings of looking at the start of an image, before they are interpreted.</summary>
internal sealed record AppleImageScan(
    long Length,
    ApmMap? Apm,
    GptTable? Gpt,
    bool HasMbrSignature,
    bool HasMbrBootCode,
    IReadOnlyList<MbrEntry> Mbr,
    bool HasIso9660,
    bool HasElTorito,
    VolumeProbeResult BareVolume,
    IReadOnlyList<AppleImagePartition> Partitions)
{
    private const byte MbrHfs = 0xAF;
    private const byte MbrAppleBoot = 0xAB;

    public static AppleImageScan Run(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek || !stream.CanRead)
        {
            throw new ArgumentException("The stream must be readable and seekable.", nameof(stream));
        }

        var apm = ApmReader.TryRead(stream);
        var gpt = GptScanner.TryRead(stream);

        Span<byte> sector = stackalloc byte[512];
        StreamReading.ReadPadded(stream, 0, sector);
        var hasSignature = BinaryPrimitives.ReadUInt16LittleEndian(sector[510..]) == 0xAA55;
        var mbr = hasSignature ? ReadMbr(sector) : [];
        var hasBootCode = hasSignature && sector[..446].ContainsAnyExcept((byte)0);

        var partitions = apm is not null ? FromApm(stream, apm)
            : gpt is not null ? FromGpt(stream, gpt)
            : FromMbr(stream, mbr);

        return new AppleImageScan(
            stream.Length,
            apm,
            gpt,
            hasSignature,
            hasBootCode,
            mbr,
            HasPrimaryVolumeDescriptor(stream),
            HasElToritoRecord(stream),
            AppleVolumeProbe.Probe(stream, 0),
            partitions);
    }

    public bool HasApplePartitionTypes => Gpt is not null && Gpt.Entries.Any(e => AppleGptTypes.IsApple(e.Type));

    public bool HasMbrApplePartition => Mbr.Any(e => e.Type is MbrHfs or MbrAppleBoot);

    private static MbrEntry[] ReadMbr(ReadOnlySpan<byte> sector)
    {
        var entries = new List<MbrEntry>();
        for (var i = 0; i < 4; i++)
        {
            var entry = sector.Slice(446 + (i * 16), 16);
            if (entry[4] != 0)
            {
                entries.Add(new MbrEntry(entry[4], BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]), BinaryPrimitives.ReadUInt32LittleEndian(entry[12..])));
            }
        }

        return [.. entries];
    }

    private static List<AppleImagePartition> FromApm(Stream stream, ApmMap map)
    {
        var result = new List<AppleImagePartition>();
        foreach (var entry in map.Partitions)
        {
            var probe = ApmPartitionTypes.IsStructural(entry.Type)
                ? VolumeProbeResult.None
                : AppleVolumeProbe.Probe(stream, entry.StartOffset);
            result.Add(new AppleImagePartition(entry.Index, entry.Name, entry.Type, entry.StartOffset, entry.Length, probe.FileSystem, probe.BlessedFolder != 0));
        }

        return result;
    }

    private static List<AppleImagePartition> FromGpt(Stream stream, GptTable table)
    {
        var result = new List<AppleImagePartition>();
        foreach (var entry in table.Entries)
        {
            var offset = entry.FirstLba * table.SectorSize;
            var length = Math.Max(0, entry.LastLba - entry.FirstLba + 1) * table.SectorSize;
            var probe = AppleGptTypes.IsApple(entry.Type) ? AppleVolumeProbe.Probe(stream, offset) : VolumeProbeResult.None;
            result.Add(new AppleImagePartition(entry.Index, entry.Name, AppleGptTypes.Describe(entry.Type), offset, length, probe.FileSystem, probe.BlessedFolder != 0));
        }

        return result;
    }

    private static List<AppleImagePartition> FromMbr(Stream stream, MbrEntry[] entries)
    {
        var result = new List<AppleImagePartition>();
        for (var i = 0; i < entries.Length; i++)
        {
            var entry = entries[i];
            var offset = entry.StartLba * 512;
            var probe = entry.Type is MbrHfs or MbrAppleBoot ? AppleVolumeProbe.Probe(stream, offset) : VolumeProbeResult.None;
            result.Add(new AppleImagePartition(i, string.Empty, $"MBR 0x{entry.Type:X2}", offset, entry.SectorCount * 512, probe.FileSystem, probe.BlessedFolder != 0));
        }

        return result;
    }

    // Volume descriptors start at sector 16 of 2048 bytes: type 1 for the primary one, "CD001", version 1.
    private static bool HasPrimaryVolumeDescriptor(Stream stream)
    {
        Span<byte> descriptor = stackalloc byte[8];
        StreamReading.ReadPadded(stream, 16 * 2048L, descriptor);
        return descriptor[0] == 1 && descriptor.Slice(1, 5).SequenceEqual("CD001"u8) && descriptor[6] == 1;
    }

    // The boot record that points at the El Torito catalog is the descriptor after the primary one.
    private static bool HasElToritoRecord(Stream stream)
    {
        Span<byte> descriptor = stackalloc byte[39];
        StreamReading.ReadPadded(stream, 17 * 2048L, descriptor);
        return descriptor[0] == 0
            && descriptor.Slice(1, 5).SequenceEqual("CD001"u8)
            && descriptor.Slice(7, 23).SequenceEqual("EL TORITO SPECIFICATION"u8);
    }
}
