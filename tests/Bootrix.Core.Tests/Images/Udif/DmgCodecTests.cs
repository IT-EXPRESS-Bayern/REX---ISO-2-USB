// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images.Udif;

namespace Bootrix.Core.Tests.Images.Udif;

/// <summary>Decoder checks against chunks that were compressed by the reference encoders.</summary>
public class DmgCodecTests
{
    public static TheoryData<string> LzfseNames => [.. CodecFixtures.Lzfse.Select(f => f.Name)];

    public static TheoryData<string> XzNames => [.. CodecFixtures.Xz.Select(f => f.Name)];

    [Theory]
    [MemberData(nameof(LzfseNames))]
    public void Lzfse_ReferenceEncoderOutput_DecodesToPlainData(string name)
    {
        AssertSingleChunkImageDecodes(CodecFixtures.Lzfse.Single(f => f.Name == name));
    }

    [Theory]
    [MemberData(nameof(XzNames))]
    public void Xz_ReferenceEncoderOutput_DecodesToPlainData(string name)
    {
        AssertSingleChunkImageDecodes(CodecFixtures.Xz.Single(f => f.Name == name));
    }

    [Fact]
    public void AllCodecsTogether_InOneImage_ReturnTheConcatenatedData()
    {
        var fixtures = CodecFixtures.Lzfse.Concat(CodecFixtures.Xz).ToList();
        var chunks = fixtures.Select(f => f.ToChunk()).ToList();
        chunks.Insert(1, ChunkSpec.Zero(8));
        chunks.Add(ChunkSpec.Ignore(4));
        var expected = new MemoryStream();
        foreach (var chunk in chunks)
        {
            expected.Write(chunk.Plain ?? new byte[chunk.SectorCount * 512]);
        }

        var image = new UdifBuilder().AddPartition(0, "disk image (Apple_HFS : 1)", 0, chunks).Build();

        using var reader = DmgReader.Open(new MemoryStream(image));
        Assert.Equal(expected.ToArray(), DmgFixtures.ReadAll(reader));
        Assert.Contains(UdifChunkType.Lzfse, reader.Info.ChunkTypes);
        Assert.Contains(UdifChunkType.Xz, reader.Info.ChunkTypes);
        reader.VerifyChecksums().ThrowIfInvalid();
    }

    [Fact]
    public void Adc_RunLengthAndLongMatches_DecodeCorrectly()
    {
        var data = new byte[4096];
        Array.Fill(data, (byte)0xAB, 0, 1500);
        ImageTestData.Text(2000, 5).CopyTo(data, 1500);
        Array.Fill(data, (byte)0x01, 3500, 596);

        var image = new UdifBuilder().AddPartition(0, "x (Apple_HFS : 1)", 0, [ChunkSpec.Adc(data)]).Build();

        using var reader = DmgReader.Open(new MemoryStream(image));
        Assert.Equal(data, DmgFixtures.ReadAll(reader));
    }

    [Fact]
    public void Bzip2_LargeChunk_DecodesCorrectly()
    {
        var data = ImageTestData.Text(8 * 1024 * 1024, 77);
        var image = new UdifBuilder().AddPartition(0, "x (Apple_HFS : 1)", 0, [ChunkSpec.Bzip2(data)]).Build();

        using var reader = DmgReader.Open(new MemoryStream(image));
        Assert.Equal(data, DmgFixtures.ReadAll(reader));
    }

    private static void AssertSingleChunkImageDecodes(CodecFixture fixture)
    {
        var chunk = fixture.ToChunk();
        var image = new UdifBuilder().AddPartition(0, "disk image (Apple_HFS : 1)", 0, [chunk]).Build();

        using var reader = DmgReader.Open(new MemoryStream(image));

        Assert.Equal(fixture.Plain(), DmgFixtures.ReadAll(reader));
    }
}
