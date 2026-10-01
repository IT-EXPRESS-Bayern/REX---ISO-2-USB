// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Boot.Dos;
using Bootrix.Core.Errors;
using Bootrix.Core.FileSystems.Fat;
using Bootrix.Core.IO;
using Bootrix.Core.Planning;
using Bootrix.Core.Storage;

namespace Bootrix.Core.Writing.Dos;

/// <summary>
/// Writes a FAT volume that starts at LBA 0 of the device and has no partition table, as on a diskette: formats it,
/// copies the files of a DOS system disk (if there is one) and writes the result to the device.
/// </summary>
public static class SuperfloppyWriter
{
    /// <summary>Volumes up to this size are assembled in memory and written front to back, which is what a diskette drive is good at.</summary>
    private const long InMemoryLimit = 8L * 1024 * 1024;

    private const int ChunkBytes = 64 * 1024;
    private const long WipeBytes = 1024 * 1024;

    /// <param name="device">The whole device, locked and dismounted by the caller.</param>
    /// <param name="plan">A plan with <see cref="MediaPlan.Superfloppy"/> set, which has exactly one partition at offset 0.</param>
    /// <param name="system">The DOS system to install; null leaves an empty volume with the "not bootable" stub.</param>
    public static void Write(
        IBlockDevice device,
        MediaPlan plan,
        DosSystem? system,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(plan);

        var partition = plan.Superfloppy && plan.Partitions is [{ StartBytes: 0 } only] ? only : null;
        if (partition is null)
        {
            throw new InvalidOperationException("The plan is not for a medium without partition table.");
        }

        if (device.SectorSize != plan.SectorSize || partition.LengthBytes > device.Length)
        {
            throw new BootrixException(ErrorCode.DeviceChanged, $"the device has {device.Length} bytes of {device.SectorSize}, the plan {partition.LengthBytes} of {plan.SectorSize}");
        }

        var options = plan.ToFatOptions(partition);
        if (system is not null)
        {
            options = system.Customize(options, plan.TotalSectors);
        }

        if (partition.LengthBytes <= InMemoryLimit)
        {
            WriteFromMemory(device, partition.LengthBytes, options, system, progress, cancellationToken);
        }
        else
        {
            WriteInPlace(device, partition.LengthBytes, options, system, progress, cancellationToken);
        }

        device.Flush();
    }

    private static void WriteFromMemory(
        IBlockDevice device,
        long length,
        FatFormatOptions options,
        DosSystem? system,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var image = new byte[length];
        using (var volume = new MemoryStream(image))
        {
            Fill(volume, options, system, cancellationToken);
        }

        // Unbuffered device I/O wants buffers at a sector-aligned address, which a managed array does not have.
        using (var chunk = new AlignedBuffer(ChunkBytes, device.BufferAlignment))
        {
            for (long offset = 0; offset < length; offset += ChunkBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var take = (int)Math.Min(ChunkBytes, length - offset);
                image.AsSpan((int)offset, take).CopyTo(chunk.GetSpan());
                device.Write(offset, chunk.GetSpan()[..take]);
                progress?.Report((double)(offset + take) / length / 2);
            }
        }

        device.Flush();
        Verify(device, image, progress, cancellationToken);
    }

    /// <summary>Reads the volume back sector by sector; a diskette that was not written correctly is common enough to be worth the minute.</summary>
    private static void Verify(IBlockDevice device, byte[] expected, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        using var chunk = new AlignedBuffer(ChunkBytes, device.BufferAlignment);
        for (long offset = 0; offset < expected.Length; offset += ChunkBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var take = (int)Math.Min(ChunkBytes, expected.Length - offset);
            var buffer = chunk.GetSpan()[..take];
            var read = device.Read(offset, buffer);
            if (read != take || !buffer.SequenceEqual(expected.AsSpan((int)offset, take)))
            {
                throw new BootrixException(ErrorCode.VerifyMismatch, $"difference in the sectors from byte {offset}")
                {
                    Arguments = [offset],
                };
            }

            progress?.Report(0.5 + (double)(offset + take) / expected.Length / 2);
        }
    }

    private static void WriteInPlace(
        IBlockDevice device,
        long length,
        FatFormatOptions options,
        DosSystem? system,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        // Old partition tables would survive in places the new volume does not overwrite; the backup GPT at the end is the usual one.
        DiskWiper.WipeTables(device, WipeBytes, WipeBytes);
        progress?.Report(0.2);

        using var volume = new BlockDeviceStream(device, 0, length);
        Fill(volume, options, system, cancellationToken);
        volume.Flush();
        progress?.Report(1);
    }

    private static void Fill(Stream volume, FatFormatOptions options, DosSystem? system, CancellationToken cancellationToken)
    {
        FatFormatter.Format(volume, options, cancellationToken);
        if (system is not null)
        {
            DosVolumeWriter.Write(volume, system.Files, cancellationToken);
        }
    }
}
