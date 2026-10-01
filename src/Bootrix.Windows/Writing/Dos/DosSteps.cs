// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Boot.Dos;
using Bootrix.Core.Errors;
using Bootrix.Core.Jobs;
using Bootrix.Core.Partitioning;
using Bootrix.Core.Planning;
using Bootrix.Core.Storage;
using Bootrix.Core.Writing.Dos;
using Bootrix.Windows.Interop;
using Bootrix.Windows.Storage;
using Microsoft.Extensions.Logging;

namespace Bootrix.Windows.Writing.Dos;

/// <summary>The steps of the DOS and format-only writers that go beyond <see cref="StandardSteps"/>.</summary>
internal static class DosSteps
{
    public const string PrepareSystemKey = "Dos.PrepareSystem";
    public const string CopyFilesKey = "Dos.CopyFiles";
    public const string WriteMbrKey = "Dos.WriteMbr";
    public const string SuperfloppyKey = "Dos.Superfloppy";

    /// <summary>Assembles the files and boot code of the DOS, fetching MS-DOS from Microsoft first when the user asked for it. Touches no disk.</summary>
    public static IJobStep PrepareSystem(DosJobState state, MediaWriteContext write, double weight = 3) =>
        new DelegateJobStep(PrepareSystemKey, weight, (context, ct) => state.PrepareAsync(write, context, ct));

    /// <summary>Copies the system files onto the mounted volume of the main partition, system files first.</summary>
    public static IJobStep CopyFiles(DosJobState state, MediaWriteContext write, double weight = 3) =>
        new DelegateJobStep(CopyFilesKey, weight, (context, ct) => CopyFilesAsync(state, write, context, ct));

    /// <summary>Puts the Syslinux MBR code into sector 0 and confirms the one active FAT partition the BIOS will start.</summary>
    public static IJobStep WriteMbr(MediaWriteContext write, double weight = 1) =>
        new DelegateJobStep(WriteMbrKey, weight, (_, ct) => Task.Run(() => WriteMbrCode(write, ct), ct));

    /// <summary>
    /// Writes a FAT volume that starts at LBA 0 to the whole device: a diskette, or a stick for BIOSes that start it as a diskette.
    /// <paramref name="state"/> is null for a plain format without DOS.
    /// </summary>
    public static IJobStep Superfloppy(WriteServices services, DosJobState? state, MediaWriteContext write, double weight = 8) =>
        new DelegateJobStep(SuperfloppyKey, weight, (context, ct) => SuperfloppyAsync(services, state, write, context, ct));

    private static async Task CopyFilesAsync(DosJobState state, MediaWriteContext write, JobContext context, CancellationToken cancellationToken)
    {
        for (var targetIndex = 0; targetIndex < write.Targets.Count; targetIndex++)
        {
            var target = write.Targets[targetIndex];
            var main = target.Plan.Partitions.ToList().FindIndex(p => p.Role == PartitionRole.Main);
            var volume = target.Prepared?.Partitions[main].Volume
                ?? throw new BootrixException(ErrorCode.VolumeNotMounted, $"partition {main} of disk {target.Device.DiskNumber}");

            var share = 1.0 / write.Targets.Count;
            var first = targetIndex * share;
            var progress = new Progress<double>(value => context.ReportStep(first + value * share));
            await Task.Run(() => DosVolumeCopier.Copy(volume.VolumeGuidPath, state.SystemOf(target).Files, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
    }

    private static Task WriteMbrCode(MediaWriteContext write, CancellationToken cancellationToken)
    {
        foreach (var target in write.Targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var disk = DiskAccess.Open(target.Device, write: true, cancellationToken);
            using var buffer = new AlignedBuffer(disk.SectorSize, disk.BufferAlignment);
            var sector = buffer.GetSpan();

            ReadSectorZero(disk, sector);
            var updated = DosMbr.Install(sector, forceBootDrive: target.Plan.LegacyBios);
            updated.CopyTo(sector);
            disk.Write(0, sector);
            disk.Flush();

            // Windows may rewrite the table when it picks up the new layout; the code and the flag have to survive that.
            ReadSectorZero(disk, sector);
            if (!sector[..Mbr.BootstrapLength].SequenceEqual(updated.AsSpan(0, Mbr.BootstrapLength)))
            {
                throw new BootrixException(ErrorCode.VerifyMismatch, "MBR boot code differs after writing") { Arguments = [0L] };
            }

            _ = DosMbr.Install(sector, forceBootDrive: target.Plan.LegacyBios);
            DeviceIo.TryControl(disk.Handle, Ioctl.DiskUpdateProperties);
        }

        return Task.CompletedTask;
    }

    private static void ReadSectorZero(PhysicalDisk disk, Span<byte> sector)
    {
        if (disk.Read(0, sector) != sector.Length)
        {
            throw new BootrixException(ErrorCode.ReadError, "sector 0 could not be read") { Arguments = [disk.Name] };
        }
    }

    private static async Task SuperfloppyAsync(WriteServices services, DosJobState? state, MediaWriteContext write, JobContext context, CancellationToken cancellationToken)
    {
        var log = services.LoggerFor<DosWriterLog>();
        for (var index = 0; index < write.Targets.Count; index++)
        {
            var target = write.Targets[index];
            await services.Journal.WriteAsync(
                new JournalEntry
                {
                    JobId = write.JobId,
                    Kind = "WriteImage",
                    DiskIdentity = target.Identity.ToKey(),
                    Phase = "superfloppy",
                    SourcePath = write.ImagePath,
                    StartedUtc = DateTimeOffset.UtcNow,
                },
                cancellationToken).ConfigureAwait(false);

            var share = 1.0 / write.Targets.Count;
            var first = index * share;
            var progress = new Progress<double>(value => context.ReportStep(first + value * share));
            var system = state?.SystemOf(target);

            await Task.Run(
                () =>
                {
                    // Every volume on the disk is dismounted and locked for as long as the raw handle writes, or Windows would fight over the sectors.
                    using var locks = VolumeLockSet.Acquire(target.Device, log, cancellationToken);
                    using var disk = DiskAccess.Open(target.Device, write: true, cancellationToken);
                    SuperfloppyWriter.Write(disk, target.Plan, system, progress, cancellationToken);
                    DeviceIo.TryControl(disk.Handle, Ioctl.DiskUpdateProperties);
                },
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Category for the logger of the shared steps; they are not tied to one writer class.</summary>
    private sealed class DosWriterLog;
}
