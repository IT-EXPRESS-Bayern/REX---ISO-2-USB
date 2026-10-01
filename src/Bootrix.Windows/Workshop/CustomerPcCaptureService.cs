// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Workshop;
using Bootrix.Core.Workshop.Advice;
using Bootrix.Core.Workshop.Capture;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Windows.Workshop;

/// <summary>
/// Takes the inventory of a customer PC. By default it reads names and lists only; the Wi-Fi keys, the BitLocker recovery passwords
/// and the Windows product key each need their own opt-in in <see cref="CustomerPcCaptureOptions"/>. Everything is read locally.
/// </summary>
public sealed class CustomerPcCaptureService(ILogger<CustomerPcCaptureService>? logger = null, TimeProvider? timeProvider = null) : ICustomerPcCapture
{
    private readonly ILogger _log = logger ?? NullLogger<CustomerPcCaptureService>.Instance;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<CustomerPcCapture> CaptureAsync(CustomerPcCaptureOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var issues = new IssueLog(_log);
        var notes = new List<AdvisorMessage>();

        var wlan = await ReadWlanAsync(options, issues, notes, cancellationToken).ConfigureAwait(false);
        var programs = options.IncludeInstalledPrograms
            ? await Task.Run(() => issues.Guard("installed-programs", InstalledProgramReader.Read, cancellationToken), cancellationToken).ConfigureAwait(false)
            : null;
        var drivers = options.IncludeThirdPartyDrivers
            ? await Task.Run(() => issues.Guard("driver-store", () => DriverStoreReader.Read(_log, cancellationToken), cancellationToken), cancellationToken).ConfigureAwait(false)
            : null;
        var recoveryKeys = options.IncludeBitLockerRecoveryKeys
            ? await Task.Run(() => ReadRecoveryKeys(issues, notes, cancellationToken), cancellationToken).ConfigureAwait(false)
            : null;
        var productKey = options.IncludeWindowsProductKey
            ? await Task.Run(() => issues.Guard("windows-product-key", WindowsProductKeyReader.Read, cancellationToken), cancellationToken).ConfigureAwait(false)
            : null;

        if (productKey is not null)
        {
            notes.Add(new AdvisorMessage(AdvisorKeys.CaptureProductKeyMayBeGeneric, AdvisorSeverity.Info));
        }

        var capture = new CustomerPcCapture
        {
            CapturedAt = _time.GetUtcNow(),
            WlanProfiles = wlan,
            InstalledPrograms = programs,
            ThirdPartyDrivers = drivers,
            BitLockerRecoveryKeys = recoveryKeys,
            WindowsProductKey = productKey,
            Issues = issues.Issues,
        };

        if (capture.ContainsSecrets)
        {
            notes.Insert(0, new AdvisorMessage(AdvisorKeys.CaptureSecretsIncluded, AdvisorSeverity.Warning));
        }

        _log.LogInformation(
            "Customer PC captured: {Wlan} Wi-Fi profiles, {Programs} programs, {Drivers} third-party drivers, {Keys} recovery keys",
            wlan?.Count ?? 0,
            programs?.Count ?? 0,
            drivers?.Count ?? 0,
            recoveryKeys?.Count ?? 0);

        return capture with { Notes = notes };
    }

    private static async Task<IReadOnlyList<WlanProfile>?> ReadWlanAsync(CustomerPcCaptureOptions options, IssueLog issues, List<AdvisorMessage> notes, CancellationToken cancellationToken)
    {
        try
        {
            if (options.WlanExportDirectory is { Length: > 0 } directory)
            {
                var exported = await WlanProfileReader.ExportAsync(directory, cancellationToken).ConfigureAwait(false);
                notes.Add(new AdvisorMessage(AdvisorKeys.CaptureWlanExportFiles, AdvisorSeverity.Warning, directory));
                var protectedKeys = exported.Count(p => p.KeyProtected == true);
                if (protectedKeys > 0)
                {
                    notes.Add(new AdvisorMessage(AdvisorKeys.CaptureWlanKeysProtected, AdvisorSeverity.Info, protectedKeys));
                }

                return exported;
            }

            return options.ListWlanProfiles ? await WlanProfileReader.ListAsync(cancellationToken).ConfigureAwait(false) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            issues.Add("wlan", ex);
            return null;
        }
    }

    private static IReadOnlyList<BitLockerRecoveryKey>? ReadRecoveryKeys(IssueLog issues, List<AdvisorMessage> notes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var (keys, volumesWithoutKey) = BitLockerRecoveryReader.Read();
            notes.AddRange(volumesWithoutKey.Select(volume => new AdvisorMessage(AdvisorKeys.CaptureRecoveryKeyUnavailable, AdvisorSeverity.Info, volume)));
            return keys;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            issues.Add("bitlocker-recovery", ex);
            return null;
        }
    }
}
