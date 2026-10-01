// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Workshop.Advice;

public enum DriverNeedKind
{
    IntelVmd,
    IntelRstRaid,
    AmdRaid,
    HardwareRaid,
    VirtualStorage,
    NetworkNoDriver,
    NetworkVendorDriver,
    NetworkVirtual,
}

public enum DriverStage
{
    /// <summary>Windows Setup needs the driver itself, otherwise it finds no drive: it goes into the boot image or onto the media.</summary>
    Setup,

    /// <summary>Needed after the first boot, for network access in OOBE and updates.</summary>
    FirstBoot,
}

public sealed record DriverNeed
{
    public required DriverNeedKind Kind { get; init; }

    public required DriverStage Stage { get; init; }

    public required string DeviceName { get; init; }

    /// <summary>Hardware ID to match against driver packages, e.g. "PCI\VEN_8086&amp;DEV_9A0B".</summary>
    public string? HardwareId { get; init; }

    public bool IsWireless { get; init; }

    public required AdvisorMessage Message { get; init; }
}
