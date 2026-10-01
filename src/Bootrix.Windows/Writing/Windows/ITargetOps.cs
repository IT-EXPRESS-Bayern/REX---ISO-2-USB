// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Planning;
using Bootrix.Core.Storage;
using Bootrix.Core.Writing.Windows;

namespace Bootrix.Windows.Writing.Windows;

/// <summary>
/// What the steps do to the disk and its volumes below the level of files. Separate from the steps so that tests
/// can run them against a folder instead of a physical disk.
/// </summary>
internal interface ITargetOps
{
    /// <summary>The disk as Windows sees it now, with the volumes the new layout created.</summary>
    StorageDevice Current(MediaWriteTarget target);

    void FlushVolume(string volumeGuidPath);

    /// <summary>Everything written so far is on the device and the file systems are dismounted, so that reading back cannot be answered from the cache.</summary>
    void DropCaches(MediaWriteTarget target, StorageDevice device, CancellationToken cancellationToken);

    bool WriteMbr(StorageDevice device, MediaPlan plan, CancellationToken cancellationToken);

    void VerifyMbr(StorageDevice device, MediaPlan plan, CancellationToken cancellationToken);

    void VerifyBootSectors(StorageDevice device, PlannedPartition main, FatBootSectors expected, CancellationToken cancellationToken);

    void VerifyPartition(StorageDevice device, PlannedPartition partition, byte[] expected, CancellationToken cancellationToken);
}
