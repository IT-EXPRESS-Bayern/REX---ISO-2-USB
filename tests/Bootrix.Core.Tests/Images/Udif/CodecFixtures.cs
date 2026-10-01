// SPDX-License-Identifier: GPL-3.0-or-later
using System.Reflection;
using Bootrix.Core.Images.Udif;

namespace Bootrix.Core.Tests.Images.Udif;

/// <summary>
/// Chunks compressed by the reference encoders (Apple's lzfse command-line tool and xz from XZ Utils), stored as
/// embedded resources. The plain data is regenerated from <see cref="ImageTestData"/>, so only the compressed side
/// is checked in. They were produced with:
/// <c>lzfse -encode -i plain.bin -o name.lzfse</c> and <c>xz -c [--check=crc32 --block-size=65536] plain.bin &gt; name.xz</c>.
/// </summary>
internal static class CodecFixtures
{
    public static readonly IReadOnlyList<CodecFixture> Lzfse =
    [
        new("lzfse_text_3k", UdifChunkType.Lzfse, () => ImageTestData.Text(3072, 11)),
        new("lzfse_text_128k", UdifChunkType.Lzfse, () => ImageTestData.Text(131072, 12)),
        new("lzfse_random_16k", UdifChunkType.Lzfse, () => ImageTestData.Random(16384, 13)),
        new("lzfse_periodic_1_5m", UdifChunkType.Lzfse, () => ImageTestData.Periodic(1536 * 1024, 997, 14)),
    ];

    public static readonly IReadOnlyList<CodecFixture> Xz =
    [
        new("xz_text_128k_crc64", UdifChunkType.Xz, () => ImageTestData.Text(131072, 21)),
        new("xz_periodic_1m_crc32", UdifChunkType.Xz, () => ImageTestData.Periodic(1024 * 1024, 4093, 22)),
        new("xz_text_256k_4blocks", UdifChunkType.Xz, () => ImageTestData.Text(262144, 23)),
    ];

    public static byte[] LoadStored(string name)
    {
        var assembly = typeof(CodecFixtures).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith("." + name + ".bin", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}

internal sealed record CodecFixture(string Name, UdifChunkType Type, Func<byte[]> Plain)
{
    public ChunkSpec ToChunk() => ChunkSpec.Precompressed(Type, CodecFixtures.LoadStored(Name), Plain());
}
