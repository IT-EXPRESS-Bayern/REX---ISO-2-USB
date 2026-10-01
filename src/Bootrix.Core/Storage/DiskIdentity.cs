// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Storage;

/// <summary>
/// Fingerprint of a disk taken when the user selects it and checked again immediately before
/// the first destructive write. A different stick that took over the same disk number, or a
/// stick whose partition table changed in between, must never be written by mistake.
/// </summary>
public sealed record DiskIdentity
{
    public required string DevicePath { get; init; }

    public string? DeviceGuid { get; init; }

    public string? Serial { get; init; }

    public long SizeBytes { get; init; }

    public string? PartitionSignature { get; init; }

    /// <summary>SHA-256 over the first and last sectors (partition tables), hex.</summary>
    public string? TableHash { get; init; }

    public static DiskIdentity From(StorageDevice device, string? tableHash = null) => new()
    {
        DevicePath = device.DevicePath,
        DeviceGuid = device.DeviceGuid,
        Serial = device.Serial,
        SizeBytes = device.SizeBytes,
        PartitionSignature = device.PartitionSignature,
        TableHash = tableHash,
    };

    public bool Matches(DiskIdentity other)
    {
        if (!string.Equals(DevicePath, other.DevicePath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (SizeBytes != other.SizeBytes)
        {
            return false;
        }

        // Absent values on either side cannot be compared; present ones must agree.
        return Agree(DeviceGuid, other.DeviceGuid)
            && Agree(Serial, other.Serial)
            && Agree(PartitionSignature, other.PartitionSignature)
            && Agree(TableHash, other.TableHash);
    }

    public string ToKey() => string.Join('|', DevicePath.ToLowerInvariant(), DeviceGuid, Serial, SizeBytes, PartitionSignature);

    private static bool Agree(string? a, string? b) =>
        a is null || b is null || string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
