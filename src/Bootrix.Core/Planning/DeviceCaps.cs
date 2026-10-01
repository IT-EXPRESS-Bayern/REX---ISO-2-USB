// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Planning;

/// <summary>What the planner needs to know about the target device; filled in by the platform layer.</summary>
public sealed record DeviceCaps
{
    public required long SizeBytes { get; init; }

    /// <summary>The sector size the operating system addresses the device with: 512, or 4096 on 4Kn media and some USB bridges.</summary>
    public int LogicalSectorSize { get; init; } = 512;

    public int PhysicalSectorSize { get; init; } = 512;

    public bool Removable { get; init; } = true;

    public DeviceBus Bus { get; init; } = DeviceBus.Usb;

    public DeviceMedium Medium { get; init; } = DeviceMedium.Stick;
}
