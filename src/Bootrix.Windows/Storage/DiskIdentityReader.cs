// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using Bootrix.Core.Storage;

namespace Bootrix.Windows.Storage;

/// <summary>Takes the fingerprint that ties a user's confirmation to one specific disk in one specific state.</summary>
public static class DiskIdentityReader
{
    private const int HeadSectors = 34;
    private const int TailSectors = 33;

    public static DiskIdentity Capture(StorageDevice device)
    {
        using var disk = DiskAccess.Open(device, write: false);
        return DiskIdentity.From(device, HashTables(disk, device));
    }

    /// <summary>
    /// Looks at the disk again right before the first write. Throws when the disk is not the one
    /// the user confirmed, or when its partition tables changed since then.
    /// </summary>
    public static void EnsureUnchanged(IDiskService disks, StorageDevice confirmed, DiskIdentity expected)
    {
        var current = disks.Find(confirmed.DevicePath)
            ?? throw new Core.Errors.BootrixException(Core.Errors.ErrorCode.DeviceNotFound, confirmed.DevicePath);

        var now = Capture(current);
        if (!expected.Matches(now))
        {
            throw new Core.Errors.BootrixException(Core.Errors.ErrorCode.DeviceChanged, $"{expected.ToKey()} != {now.ToKey()}");
        }
    }

    internal static string HashTables(PhysicalDisk disk, StorageDevice device)
    {
        var sector = device.LogicalSectorSize;
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var buffer = new AlignedBuffer(Math.Max(HeadSectors, TailSectors) * sector, disk.BufferAlignment);

        var headBytes = (int)Math.Min(HeadSectors * (long)sector, device.SizeBytes);
        var read = disk.Read(0, buffer.GetSpan()[..headBytes]);
        sha.AppendData(buffer.GetSpan()[..read]);

        var tailBytes = (int)Math.Min(TailSectors * (long)sector, device.SizeBytes);
        if (device.SizeBytes > headBytes + tailBytes)
        {
            read = disk.Read(device.SizeBytes - tailBytes, buffer.GetSpan()[..tailBytes]);
            sha.AppendData(buffer.GetSpan()[..read]);
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }
}
