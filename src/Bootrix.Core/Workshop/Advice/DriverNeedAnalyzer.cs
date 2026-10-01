// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Workshop.Hardware;

namespace Bootrix.Core.Workshop.Advice;

/// <summary>
/// Finds the hardware that Windows Setup or the first boot cannot drive with what ships on the media. Storage controllers are
/// judged by their IDs and class; for network adapters the running system is the evidence: a driver that came from the driver
/// store (oemNN.inf) or is missing altogether is not part of Windows.
/// </summary>
internal static class DriverNeedAnalyzer
{
    private const int ProblemNotConfigured = 1;
    private const int ProblemFailedInstall = 28;

    public static IReadOnlyList<DriverNeed> Analyze(TargetPcInfo info)
    {
        var needs = new List<DriverNeed>();
        foreach (var device in info.Devices ?? [])
        {
            var need = device.Category switch
            {
                DeviceCategory.Storage => ForStorage(device),
                DeviceCategory.Network => ForNetwork(device),
                _ => null,
            };

            if (need is not null && !needs.Any(n => n.Kind == need.Kind && n.HardwareId == need.HardwareId))
            {
                needs.Add(need);
            }
        }

        return needs;
    }

    private static DriverNeed? ForStorage(HardwareDevice device)
    {
        var (kind, key) = StorageControllerClassifier.Classify(device) switch
        {
            StorageControllerKind.IntelVmd => (DriverNeedKind.IntelVmd, AdvisorKeys.DriverIntelVmd),
            StorageControllerKind.IntelRstRaid => (DriverNeedKind.IntelRstRaid, AdvisorKeys.DriverIntelRstRaid),
            StorageControllerKind.AmdRaid => (DriverNeedKind.AmdRaid, AdvisorKeys.DriverAmdRaid),
            StorageControllerKind.HardwareRaid => (DriverNeedKind.HardwareRaid, AdvisorKeys.DriverHardwareRaid),
            StorageControllerKind.VirtualStorage => (DriverNeedKind.VirtualStorage, AdvisorKeys.DriverVirtualStorage),
            _ => (default(DriverNeedKind?), ""),
        };

        if (kind is null)
        {
            return null;
        }

        var hardwareId = HardwareIdOf(device);
        return new DriverNeed
        {
            Kind = kind.Value,
            Stage = DriverStage.Setup,
            DeviceName = device.DisplayName,
            HardwareId = hardwareId,
            Message = new AdvisorMessage(key, AdvisorSeverity.Critical, device.DisplayName, ShortId(device, hardwareId)),
        };
    }

    private static DriverNeed? ForNetwork(HardwareDevice device)
    {
        var wireless = device.IsWireless;
        var hardwareId = HardwareIdOf(device);
        var shortId = ShortId(device, hardwareId);

        if (device.Pci is { } pci && StorageControllerTable.VirtualNetwork.Contains(pci))
        {
            return Network(device, DriverNeedKind.NetworkVirtual, AdvisorKeys.DriverNetworkVirtual, hardwareId, wireless, device.DisplayName, shortId);
        }

        if (device.ProblemCode is ProblemNotConfigured or ProblemFailedInstall)
        {
            var key = wireless ? AdvisorKeys.DriverWirelessNoDriver : AdvisorKeys.DriverNetworkNoDriver;
            return Network(device, DriverNeedKind.NetworkNoDriver, key, hardwareId, wireless, device.DisplayName, shortId, device.ProblemCode);
        }

        if (device.Driver is { IsVendorPackage: true } driver)
        {
            var key = wireless ? AdvisorKeys.DriverWirelessVendor : AdvisorKeys.DriverNetworkVendor;
            return Network(device, DriverNeedKind.NetworkVendorDriver, key, hardwareId, wireless, device.DisplayName, shortId, driver.Provider ?? driver.InfName);
        }

        return null;
    }

    private static DriverNeed Network(
        HardwareDevice device,
        DriverNeedKind kind,
        string key,
        string? hardwareId,
        bool wireless,
        params object?[] arguments) => new()
        {
            Kind = kind,
            Stage = DriverStage.FirstBoot,
            DeviceName = device.DisplayName,
            HardwareId = hardwareId,
            IsWireless = wireless,
            Message = new AdvisorMessage(key, AdvisorSeverity.Warning, arguments),
        };

    private static string? HardwareIdOf(HardwareDevice device) =>
        device.Pci?.HardwareId ?? (device.HardwareIds.Count > 0 ? device.HardwareIds[0] : null);

    private static string ShortId(HardwareDevice device, string? hardwareId) =>
        device.Pci?.ToString() ?? hardwareId ?? device.InstanceId;
}
