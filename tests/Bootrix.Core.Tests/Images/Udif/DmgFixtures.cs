// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images.Udif;

namespace Bootrix.Core.Tests.Images.Udif;

/// <summary>Standard images shared by the DMG tests.</summary>
internal static class DmgFixtures
{
    public const int VolumeSectors = 2000;

    /// <summary>Partitions in the shape of an hdiutil whole-disk image; together they cover the volume.</summary>
    public static readonly IReadOnlyList<(string Name, long Start, long Count)> Partitions =
    [
        ("Protective Master Boot Record (MBR : 0)", 0, 1),
        ("GPT Header (Primary GPT Header : 1)", 1, 1),
        ("GPT Partition Data (Primary GPT Table : 2)", 2, 32),
        ("Apple_HFS (Apple_HFS : 3)", 34, 1500),
        ("Apple_Free (Apple_Free : 4)", 1534, 466),
    ];

    public static byte[] Volume { get; } = ImageTestData.Volume(VolumeSectors, 20260701);

    public static byte[] Build(string codec, int chunkSectors = 64, UdifBuilder? template = null) =>
        UdifBuilder.FromVolume(Volume, Partitions, chunkSectors, (index, data) => Encode(codec, index, data), template).Build();

    public static ChunkSpec Encode(string codec, int index, byte[] data)
    {
        if (codec != "raw" && data.All(b => b == 0))
        {
            return index % 2 == 0 ? ChunkSpec.Zero(data.Length / 512) : ChunkSpec.Ignore(data.Length / 512);
        }

        return codec switch
        {
            "raw" => ChunkSpec.Raw(data),
            "zlib" => ChunkSpec.Zlib(data),
            "bzip2" => ChunkSpec.Bzip2(data),
            "adc" => ChunkSpec.Adc(data),
            "mixed" => (index % 4) switch
            {
                0 => ChunkSpec.Zlib(data),
                1 => ChunkSpec.Bzip2(data),
                2 => ChunkSpec.Adc(data),
                _ => ChunkSpec.Raw(data),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(codec), codec, null),
        };
    }

    public static DmgReader Open(byte[] image, DmgReaderOptions? options = null) =>
        DmgReader.Open(new MemoryStream(image), leaveOpen: false, options);

    public static byte[] ReadAll(Stream stream)
    {
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
