// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using Bootrix.Core.Errors;
using Bootrix.Core.Optical.Reading;
using Bootrix.Core.Storage;

namespace Bootrix.Core.Optical;

/// <summary>Reads a burned disc back through the operating system and compares it with what was meant to be written.</summary>
public static class ReadBackVerifier
{
    public static Task VerifyAsync(
        ISectorReader reader,
        ImageDigest expected,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default) =>
        Task.Factory.StartNew(() => Verify(reader, expected, progress, cancellationToken), cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static void Verify(ISectorReader reader, ImageDigest expected, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        var sectors = SectorMath.SectorsFor(expected.Length);
        if (reader.SectorCount < sectors)
        {
            throw new BootrixException(ErrorCode.VerifyMismatch, $"disc has {reader.SectorCount} sectors, image {sectors}") { Arguments = [reader.SectorCount * SectorMath.SectorSize] };
        }

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[expected.ChunkSize];
        long done = 0;
        for (var chunk = 0; chunk < expected.ChunkHashes.Count; chunk++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var offset = (long)chunk * expected.ChunkSize;
            var length = (int)Math.Min(expected.ChunkSize, expected.Length - offset);
            ReadSectors(reader, offset / SectorMath.SectorSize, (int)SectorMath.SectorsFor(length), buffer);

            if (ChunkHash.Compute(buffer.AsSpan(0, length)) != expected.ChunkHashes[chunk])
            {
                throw new BootrixException(ErrorCode.VerifyMismatch, $"chunk {chunk} differs") { Arguments = [offset] };
            }

            sha.AppendData(buffer, 0, length);
            done += length;
            progress?.Report(done);
        }

        if (!string.Equals(Convert.ToHexStringLower(sha.GetHashAndReset()), expected.Sha256, StringComparison.Ordinal))
        {
            throw new BootrixException(ErrorCode.VerifyMismatch, "SHA-256 differs") { Arguments = [0L] };
        }
    }

    // A sector that cannot be read is a read error, not a mismatch: the disc may be fine and the drive merely unable to read it back.
    private static void ReadSectors(ISectorReader reader, long lba, int count, byte[] buffer)
    {
        var done = 0;
        while (done < count)
        {
            var result = reader.Read(lba + done, count - done, buffer.AsSpan(done * SectorMath.SectorSize, (count - done) * SectorMath.SectorSize));
            if (result.SectorsRead == 0)
            {
                throw new BootrixException(ErrorCode.ReadError, $"sector {lba + done} cannot be read back") { Arguments = [lba + done] };
            }

            done += result.SectorsRead;
        }
    }
}
