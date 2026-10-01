// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using Bootrix.Core.Errors;
using Bootrix.Core.Optical;
using Bootrix.Core.Tests.Optical.Support;

namespace Bootrix.Core.Tests.Optical;

public class ReadBackVerifierTests
{
    private const int Chunk = 8 * 2048;

    private static async Task<ImageDigest> Digest(byte[] data) =>
        await ImageDigest.ComputeAsync(new MemoryStream(data), chunkSize: Chunk);

    [Fact]
    public async Task DigestMatchesAnIndependentSha256()
    {
        var data = OpticalTestData.DiscImage(100);

        var digest = await Digest(data);

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(data)), digest.Sha256);
        Assert.Equal(data.Length, digest.Length);
        Assert.Equal(13, digest.ChunkHashes.Count);
    }

    [Fact]
    public async Task DigestOfNothingIsTheEmptyHash()
    {
        var digest = await Digest([]);

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData([])), digest.Sha256);
        Assert.Empty(digest.ChunkHashes);
    }

    [Fact]
    public async Task DigestReportsProgress()
    {
        var data = OpticalTestData.DiscImage(40);
        var done = new List<long>();

        await ImageDigest.ComputeAsync(new MemoryStream(data), new Progress<long>(done.Add), Chunk);
        await Task.Delay(50);

        Assert.Equal(data.Length, done.Max());
    }

    [Fact]
    public async Task UntouchedDiscVerifies()
    {
        var data = OpticalTestData.DiscImage(100);

        await ReadBackVerifier.VerifyAsync(new FakeSectorReader(data), await Digest(data));
    }

    [Fact]
    public async Task DiscLargerThanTheImageVerifiesTheImagePartOnly()
    {
        var data = OpticalTestData.DiscImage(100);
        var disc = new byte[200 * 2048];
        data.CopyTo(disc, 0);
        new Random(9).NextBytes(disc.AsSpan(data.Length));

        await ReadBackVerifier.VerifyAsync(new FakeSectorReader(disc), await Digest(data));
    }

    [Fact]
    public async Task ChangedByteIsFoundWithItsChunkOffset()
    {
        var data = OpticalTestData.DiscImage(100);
        var digest = await Digest(data);
        var disc = (byte[])data.Clone();
        disc[2 * Chunk + 5] ^= 1;

        var ex = await Assert.ThrowsAsync<BootrixException>(() => ReadBackVerifier.VerifyAsync(new FakeSectorReader(disc), digest));

        Assert.Equal(ErrorCode.VerifyMismatch, ex.Code);
        Assert.Equal(2L * Chunk, ex.Arguments[0]);
    }

    [Fact]
    public async Task LastPartialChunkIsChecked()
    {
        var data = OpticalTestData.DiscImage(100);
        var digest = await Digest(data);
        var disc = (byte[])data.Clone();
        disc[^1] ^= 0x80;

        var ex = await Assert.ThrowsAsync<BootrixException>(() => ReadBackVerifier.VerifyAsync(new FakeSectorReader(disc), digest));

        Assert.Equal(12L * Chunk, ex.Arguments[0]);
    }

    [Fact]
    public async Task UnreadableSectorIsAReadErrorNotAMismatch()
    {
        var data = OpticalTestData.DiscImage(100);
        var digest = await Digest(data);
        var reader = new FakeSectorReader(data);
        reader.PermanentlyBad.Add(70);

        var ex = await Assert.ThrowsAsync<BootrixException>(() => ReadBackVerifier.VerifyAsync(reader, digest));

        Assert.Equal(ErrorCode.ReadError, ex.Code);
        Assert.Equal(70L, ex.Arguments[0]);
    }

    [Fact]
    public async Task DiscShorterThanTheImageFails()
    {
        var data = OpticalTestData.DiscImage(100);
        var digest = await Digest(data);

        var ex = await Assert.ThrowsAsync<BootrixException>(() => ReadBackVerifier.VerifyAsync(new FakeSectorReader(data[..(50 * 2048)], sectorCount: 50), digest));

        Assert.Equal(ErrorCode.VerifyMismatch, ex.Code);
    }

    [Fact]
    public async Task ShortReadsAreHandled()
    {
        var data = OpticalTestData.DiscImage(100);

        await ReadBackVerifier.VerifyAsync(new FakeSectorReader(data) { MaxSectorsPerRead = 3 }, await Digest(data));
    }

    [Fact]
    public async Task VerificationCanBeCancelled()
    {
        var data = OpticalTestData.DiscImage(200);
        var digest = await Digest(data);
        using var cts = new CancellationTokenSource();
        var reader = new FakeSectorReader(data) { BeforeRead = (lba, _) => { if (lba >= 32) cts.Cancel(); } };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ReadBackVerifier.VerifyAsync(reader, digest, null, cts.Token));
    }
}
