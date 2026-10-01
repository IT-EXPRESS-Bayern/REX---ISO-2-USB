// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;

namespace Bootrix.Core.Images.Udif;

public enum DmgChecksumStatus
{
    /// <summary>The image carries no checksum for this item.</summary>
    NotPresent,
    Valid,
    Mismatch,

    /// <summary>A checksum type other than CRC-32; it cannot be verified.</summary>
    Unsupported,
}

public readonly record struct DmgChecksumResult(DmgChecksumStatus Status, uint Expected, uint Actual);

public sealed record DmgPartitionChecksum(DmgPartitionInfo Partition, DmgChecksumResult Result);

/// <summary>Outcome of <see cref="DmgReader.VerifyChecksums"/>: data fork, every partition and the master checksum.</summary>
public sealed record DmgChecksumReport(
    DmgChecksumResult DataFork,
    IReadOnlyList<DmgPartitionChecksum> Partitions,
    DmgChecksumResult Master)
{
    public bool IsValid =>
        DataFork.Status != DmgChecksumStatus.Mismatch
        && Master.Status != DmgChecksumStatus.Mismatch
        && Partitions.All(p => p.Result.Status != DmgChecksumStatus.Mismatch);

    public void ThrowIfInvalid()
    {
        if (!IsValid)
        {
            throw new BootrixException(ErrorCode.ImageHashMismatch, "UDIF checksum mismatch");
        }
    }
}
