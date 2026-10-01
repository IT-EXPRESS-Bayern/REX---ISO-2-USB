// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Storage;

namespace Bootrix.Windows.Storage;

/// <summary>
/// After a new layout is applied, Windows mounts the partitions asynchronously. A volume is
/// recognised by the disk and the byte offset it lives at, not by a label or a drive letter,
/// so a second stick with the same label can never be mistaken for the new one.
/// </summary>
internal static class VolumeArrivalWaiter
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    public static async Task<VolumeInfo> WaitAsync(int diskNumber, long partitionOffset, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var match = VolumeCatalog.Load()
                .Select(v => v.Info)
                .FirstOrDefault(v => v.Extents.Any(e => e.DiskNumber == diskNumber && e.StartingOffset == partitionOffset));
            if (match is not null)
            {
                return match;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new BootrixException(ErrorCode.VolumeNotMounted, $"disk {diskNumber} offset {partitionOffset}");
            }

            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}
