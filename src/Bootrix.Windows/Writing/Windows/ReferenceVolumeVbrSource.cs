// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Model;
using Bootrix.Core.Writing.Windows;
using Bootrix.Windows.FileSystems;
using Bootrix.Windows.Interop;
using Bootrix.Windows.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Windows.Writing.Windows;

/// <summary>
/// Gets the boot code of a FAT32 Windows medium by letting Windows make it: a tiny VHD with a FAT32 partition is
/// attached, formatted by the system's own formatter, and the reserved sectors are read back. No Microsoft
/// boot code is part of Bootrix, and the boot code that results is the one of the Windows that runs the job.
/// </summary>
/// <remarks>
/// Whether the boot sector that fmifs writes on a given Windows build loads BOOTMGR is checked every time
/// (<see cref="WindowsFatBootCode.FromReservedArea"/> looks for the loader name). If it does not, the job stops
/// before the target is touched.
/// </remarks>
public sealed class ReferenceVolumeVbrSource(ILogger<ReferenceVolumeVbrSource>? logger = null) : IVbrCodeSource
{
    private const int ReservedSectors = 32;
    private const int FormatAttempts = 3;
    private static readonly TimeSpan VolumeTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan FormatRetryDelay = TimeSpan.FromSeconds(2);

    private readonly ILogger _log = logger ?? NullLogger<ReferenceVolumeVbrSource>.Instance;

    public async Task<FatBootSectors> ReadFat32Async(string scratchDirectory, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(scratchDirectory);
        Directory.CreateDirectory(scratchDirectory);
        var path = Path.Combine(scratchDirectory, "reference-" + Guid.NewGuid().ToString("N")[..8] + ".vhd");
        try
        {
            await Task.Run(
                () =>
                {
                    using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                    ReferenceDisk.Write(file);
                },
                cancellationToken).ConfigureAwait(false);

            // The disk is detached before the file is deleted: the using block ends first.
            FatBootSectors code;
            using (var disk = await Task.Run(() => TemporaryVirtualDisk.Attach(path), cancellationToken).ConfigureAwait(false))
            {
                var volume = await VolumeArrivalWaiter.WaitAsync(disk.DiskNumber, ReferenceDisk.PartitionOffset, VolumeTimeout, cancellationToken).ConfigureAwait(false);
                await FormatAsync(volume.VolumeGuidPath, cancellationToken).ConfigureAwait(false);
                code = WindowsFatBootCode.FromReservedArea(ReadReservedArea(volume.VolumeGuidPath));
            }

            _log.LogInformation("Took {Sectors} boot sectors from a FAT32 volume formatted by Windows", code.SectorCount);
            return code;
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or BootrixException { Code: ErrorCode.BootCodeUnavailable }))
        {
            throw new BootrixException(ErrorCode.BootCodeUnavailable, ex.Message, ex) { Arguments = [ex.Message] };
        }
        finally
        {
            TryDelete(path);
        }
    }

    private async Task FormatAsync(string volumePath, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await FmifsFormatter.FormatAsync(
                    new FormatRequest(volumePath, FileSystemKind.Fat32, "BOOTRIX", 0, Quick: true, Removable: false),
                    null,
                    _log,
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (BootrixException ex) when (attempt < FormatAttempts && ex.Code == ErrorCode.ExternalToolFailed)
            {
                // Explorer or the virus scanner may hold the new volume for a moment, which makes FormatEx fail with "in use".
                _log.LogDebug(ex, "Formatting the reference volume failed (attempt {Attempt}); trying again", attempt);
                await Task.Delay(FormatRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static byte[] ReadReservedArea(string volumePath)
    {
        using var handle = DeviceIo.Open(volumePath.TrimEnd('\\'), Kernel32.GenericRead, Kernel32.FileShareRead | Kernel32.FileShareWrite);
        var area = new byte[ReservedSectors * 512];
        var read = RandomAccess.Read(handle, area, 0);
        return read == area.Length ? area : throw new IOException($"read {read} of {area.Length} bytes from the reference volume");
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // The scratch folder is deleted with the job; a file that is still mapped for a moment is not worth failing for.
        }
    }
}
