// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Workshop.Hardware;

namespace Bootrix.Core.Workshop;

public sealed record CpuInfo
{
    /// <summary>Vendor identification string, e.g. GenuineIntel or AuthenticAMD.</summary>
    public string? Vendor { get; init; }

    public string? Name { get; init; }

    /// <summary>Architecture of the machine, not of the Bootrix process; a 32-bit Windows on a 64-bit CPU reports X86 here.</summary>
    public CpuArchitecture Architecture { get; init; }

    /// <summary>Whether the CPU itself can run 64-bit code, independent of the installed Windows.</summary>
    public bool? Is64BitCapable { get; init; }

    public int? PhysicalCores { get; init; }

    public int? LogicalProcessors { get; init; }

    /// <summary>Nominal clock in MHz as the firmware reports it.</summary>
    public int? MaxClockMhz { get; init; }

    /// <summary>Instruction sets of the hardware; null when they cannot be measured reliably, for example under emulation.</summary>
    public CpuFeatures? Features { get; init; }

    public bool? HypervisorPresent { get; init; }

    public string? HypervisorVendor { get; init; }
}
