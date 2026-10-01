// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Workshop.Hardware;

namespace Bootrix.Core.Workshop;

/// <summary>A data source that could not be read. The affected fields stay null, so the assessment reports them as unknown.</summary>
public sealed record CollectionIssue(string Source, string Message);

/// <summary>
/// What the collector found out about a PC. It is plain data: every section is null when its source failed or was not
/// available, and nothing in here is a judgement. <see cref="Advice.TargetPcAdvisor"/> turns it into recommendations.
/// </summary>
public sealed record TargetPcInfo
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public DateTimeOffset? CollectedAt { get; init; }

    /// <summary>Whether the process had administrator rights; TPM state, BitLocker and Secure Boot variables depend on it.</summary>
    public bool? IsElevated { get; init; }

    public CpuInfo? Cpu { get; init; }

    public FirmwareInfo? Firmware { get; init; }

    public MachineIdentity? Machine { get; init; }

    public TpmInfo? Tpm { get; init; }

    public MemoryInfo? Memory { get; init; }

    public IReadOnlyList<DiskInfo>? Disks { get; init; }

    /// <summary>PCI devices and USB network adapters with their hardware IDs and bound drivers.</summary>
    public IReadOnlyList<HardwareDevice>? Devices { get; init; }

    public OemLicenseInfo? OemLicense { get; init; }

    public IReadOnlyList<BitLockerVolumeInfo>? BitLocker { get; init; }

    public RunningSystemInfo? RunningSystem { get; init; }

    public SecureBootCertificateInfo? SecureBootCertificates { get; init; }

    public IReadOnlyList<CollectionIssue> Issues { get; init; } = [];

    public IEnumerable<HardwareDevice> DevicesOf(DeviceCategory category) =>
        (Devices ?? []).Where(d => d.Category == category);
}
