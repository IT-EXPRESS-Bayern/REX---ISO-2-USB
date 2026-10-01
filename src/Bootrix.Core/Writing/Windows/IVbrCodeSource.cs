// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Writing.Windows;

/// <summary>
/// The reserved sectors of a FAT32 volume as they are after Windows has formatted it: boot sector, the
/// sectors that continue its code, with the FSInfo sector left empty. This is the boot code that loads
/// BOOTMGR; Bootrix does not carry it, it takes what the running Windows writes itself.
/// </summary>
/// <param name="Sectors">Sector 0 up to and including the last sector that holds code, <see cref="BytesPerSector"/> each.</param>
public sealed record FatBootSectors(byte[] Sectors, int BytesPerSector = 512)
{
    public int SectorCount => Sectors.Length / BytesPerSector;
}

/// <summary>Where the boot code for the BIOS start of a FAT32 Windows medium comes from.</summary>
public interface IVbrCodeSource
{
    /// <param name="scratchDirectory">A folder the source may use for temporary files; it is deleted by the caller.</param>
    /// <exception cref="Errors.BootrixException">The boot code could not be obtained, or it is not the boot code that loads BOOTMGR.</exception>
    Task<FatBootSectors> ReadFat32Async(string scratchDirectory, CancellationToken cancellationToken);
}

/// <summary>Asks the inner source once; the boot code does not change for the lifetime of the process. A failed attempt is not remembered.</summary>
public sealed class CachingVbrCodeSource(IVbrCodeSource inner) : IVbrCodeSource
{
    private readonly Lock _gate = new();
    private Task<FatBootSectors>? _pending;

    public async Task<FatBootSectors> ReadFat32Async(string scratchDirectory, CancellationToken cancellationToken)
    {
        Task<FatBootSectors> task;
        lock (_gate)
        {
            task = _pending ??= inner.ReadFat32Async(scratchDirectory, cancellationToken);
        }

        try
        {
            return await task.ConfigureAwait(false);
        }
        catch
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pending, task))
                {
                    _pending = null;
                }
            }

            throw;
        }
    }
}
