// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Compression;
using Bootrix.Core.Images.Udif;
using SharpCompress.Compressors.BZip2;

namespace Bootrix.Core.Tests.Images.Udif;

/// <summary>One chunk as the test writer lays it out: its type, how many sectors it stands for and the stored bytes.</summary>
internal sealed record ChunkSpec(UdifChunkType Type, long SectorCount, byte[] Stored, byte[]? Plain)
{
    public static ChunkSpec Zero(long sectors) => new(UdifChunkType.ZeroFill, sectors, [], null);

    public static ChunkSpec Ignore(long sectors) => new(UdifChunkType.Ignore, sectors, [], null);

    public static ChunkSpec Raw(byte[] data) => new(UdifChunkType.Raw, data.Length / 512, data, data);

    public static ChunkSpec Zlib(byte[] data)
    {
        using var buffer = new MemoryStream();
        using (var zlib = new ZLibStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(data);
        }

        return new ChunkSpec(UdifChunkType.Zlib, data.Length / 512, buffer.ToArray(), data);
    }

    public static ChunkSpec Bzip2(byte[] data)
    {
        using var buffer = new MemoryStream();
        using (var bzip2 = BZip2Stream.Create(buffer, SharpCompress.Compressors.CompressionMode.Compress, decompressConcatenated: false, leaveOpen: true))
        {
            bzip2.Write(data);
            bzip2.Finish();
        }

        return new ChunkSpec(UdifChunkType.Bzip2, data.Length / 512, buffer.ToArray(), data);
    }

    public static ChunkSpec Adc(byte[] data) => new(UdifChunkType.Adc, data.Length / 512, AdcEncoder.Encode(data), data);

    /// <summary>A chunk whose stored bytes come from an external encoder.</summary>
    public static ChunkSpec Precompressed(UdifChunkType type, byte[] stored, byte[] plain) =>
        new(type, plain.Length / 512, stored, plain);

    public bool HasData => Type is not (UdifChunkType.ZeroFill or UdifChunkType.Ignore);
}
