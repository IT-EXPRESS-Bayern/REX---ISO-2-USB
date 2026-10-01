// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Security;
using System.Security.Principal;
using Bootrix.Core.Storage;
using Bootrix.Core.Workshop;
using Bootrix.Core.Workshop.Firmware;
using Bootrix.Core.Workshop.Hardware;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Windows.Workshop;

/// <summary>
/// Measures the PC: CPU, firmware, TPM, memory, disks, PCI devices, OEM key, BitLocker and the running Windows. The sources are
/// independent and slow (WMI), so they run side by side; each is guarded on its own and a failure only leaves its part null.
/// Nothing leaves the machine.
/// </summary>
public sealed class TargetPcCollector(IDiskService diskService, ILogger<TargetPcCollector>? logger = null, TimeProvider? timeProvider = null) : ITargetPcCollector
{
    private readonly ILogger _log = logger ?? NullLogger<TargetPcCollector>.Instance;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<TargetPcInfo> CollectAsync(CancellationToken cancellationToken = default)
    {
        var issues = new IssueLog(_log);
        var elevated = IsElevated();

        var cpu = Run(() => issues.Guard("cpu", () => CpuReader.Read(issues), cancellationToken), cancellationToken);
        var smbios = Run(() => issues.Guard("smbios", SmbiosReader.Read, cancellationToken), cancellationToken);
        var firmwareType = Run(() => FirmwareTypeOrUnknown(issues, cancellationToken), cancellationToken);
        var certificates = Run(() => issues.Guard("secure-boot-certificates", FirmwareReader.ReadCertificates, cancellationToken), cancellationToken);
        var tpm = Run(() => issues.Guard("tpm", () => TpmReader.Read(issues), cancellationToken), cancellationToken);
        var memory = Run(() => issues.Guard("memory", MemoryReader.Read, cancellationToken), cancellationToken);
        var disks = Run(() => issues.Guard("disks", () => DiskReader.Read(diskService, issues), cancellationToken), cancellationToken);
        var devices = Run(() => issues.Guard("devices", () => PnpDeviceReader.Read(issues), cancellationToken), cancellationToken);
        var oem = Run(() => issues.Guard("oem-license", () => OemKeyReader.Read(issues), cancellationToken), cancellationToken);
        var bitLocker = Run(() => issues.Guard("bitlocker", BitLockerReader.Read, cancellationToken), cancellationToken);
        var running = Run(() => issues.Guard("running-system", RunningSystemReader.Read, cancellationToken), cancellationToken);

        await Task.WhenAll(cpu, smbios, firmwareType, certificates, tpm, memory, disks, devices, oem, bitLocker, running).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var type = await firmwareType.ConfigureAwait(false);
        var smbiosData = await smbios.ConfigureAwait(false);
        var cpuInfo = await cpu.ConfigureAwait(false);

        return new TargetPcInfo
        {
            CollectedAt = _time.GetUtcNow(),
            IsElevated = elevated,
            Cpu = cpuInfo,
            Firmware = new FirmwareInfo
            {
                Type = type,
                SecureBoot = SecureBootOrUnknown(type, issues),
                Vendor = smbiosData?.BiosVendor,
                Version = smbiosData?.BiosVersion,
                ReleaseDate = smbiosData?.BiosReleaseDate,
                SmbiosVersion = smbiosData?.Version,
            },
            Machine = smbiosData is null ? null : MachineIdentity.From(smbiosData, DetectVirtualMachine(smbiosData, cpuInfo)),
            Tpm = await tpm.ConfigureAwait(false),
            Memory = await memory.ConfigureAwait(false),
            Disks = await disks.ConfigureAwait(false),
            Devices = await devices.ConfigureAwait(false),
            OemLicense = await oem.ConfigureAwait(false),
            BitLocker = await bitLocker.ConfigureAwait(false),
            RunningSystem = await running.ConfigureAwait(false),
            SecureBootCertificates = await certificates.ConfigureAwait(false),
            Issues = issues.Issues,
        };
    }

    private static Task<T> Run<T>(Func<T> work, CancellationToken cancellationToken) => Task.Run(work, cancellationToken);

    private static VirtualMachineKind DetectVirtualMachine(SmbiosData smbios, CpuInfo? cpu) =>
        VirtualizationDetector.Detect(smbios.SystemManufacturer, smbios.SystemProductName, cpu?.HypervisorVendor, cpu?.HypervisorPresent);

    private static FirmwareType FirmwareTypeOrUnknown(IssueLog issues, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return FirmwareReader.ReadType();
        }
        catch (Win32Exception ex)
        {
            issues.Add("firmware-type", ex);
            return FirmwareType.Unknown;
        }
    }

    private static SecureBootState SecureBootOrUnknown(FirmwareType type, IssueLog issues)
    {
        try
        {
            return FirmwareReader.ReadSecureBoot(type);
        }
        catch (Exception ex) when (ex is Win32Exception or SecurityException or UnauthorizedAccessException)
        {
            issues.Add("secure-boot", ex);
            return SecureBootState.Unknown;
        }
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
