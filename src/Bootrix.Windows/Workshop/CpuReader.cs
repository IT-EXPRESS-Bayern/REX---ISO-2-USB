// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Bootrix.Core.Workshop;
using Bootrix.Core.Workshop.Hardware;
using Microsoft.Win32;

namespace Bootrix.Windows.Workshop;

internal static class CpuReader
{
    private const string ProcessorKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";

    public static CpuInfo Read(IssueLog issues)
    {
        var probe = CpuFeatureProbe.Detect();

        // A process that runs under emulation sees the instruction sets of the emulator, not those of the hardware.
        var emulated = RuntimeInformation.ProcessArchitecture != RuntimeInformation.OSArchitecture;
        var architecture = ArchitectureOf(RuntimeInformation.OSArchitecture);

        string? vendor = null;
        string? name = null;
        int? registryMhz = null;
        using (var key = Registry.LocalMachine.OpenSubKey(ProcessorKey))
        {
            vendor = (key?.GetValue("VendorIdentifier") as string)?.Trim();
            name = (key?.GetValue("ProcessorNameString") as string)?.Trim();
            registryMhz = key?.GetValue("~MHz") as int?;
        }

        var (physical, logical, wmiMhz) = ReadProcessors(issues);

        return new CpuInfo
        {
            Vendor = Blank(vendor) ?? (emulated ? null : probe.Vendor),
            Name = Blank(name) ?? (emulated ? null : probe.BrandString),
            Architecture = architecture,
            Is64BitCapable = Is64Bit(architecture, emulated, probe),
            PhysicalCores = physical,
            LogicalProcessors = logical ?? Environment.ProcessorCount,
            MaxClockMhz = Positive(wmiMhz) ?? Positive(registryMhz),
            Features = emulated ? null : probe.Features,
            HypervisorPresent = emulated ? null : probe.HypervisorPresent,
            HypervisorVendor = emulated ? null : probe.HypervisorVendor,
        };
    }

    public static CpuArchitecture ArchitectureOf(Architecture architecture) => architecture switch
    {
        Architecture.X64 => CpuArchitecture.X64,
        Architecture.X86 => CpuArchitecture.X86,
        Architecture.Arm64 => CpuArchitecture.Arm64,
        Architecture.Arm => CpuArchitecture.Arm,
        _ => CpuArchitecture.Unknown,
    };

    private static bool? Is64Bit(CpuArchitecture architecture, bool emulated, CpuProbe probe) => architecture switch
    {
        CpuArchitecture.X64 or CpuArchitecture.Arm64 => true,
        CpuArchitecture.Arm => false,
        // A 32-bit Windows says nothing about the CPU; CPUID does, as long as it is the real one.
        CpuArchitecture.X86 when !emulated => probe.Is64BitCapable,
        _ => null,
    };

    /// <summary>Sums cores over all sockets. Win32_Processor needs no administrator rights but is missing in a bare Windows PE.</summary>
    private static (int? Physical, int? Logical, int? Mhz) ReadProcessors(IssueLog issues)
    {
        try
        {
            var processors = Wmi.Select(
                "SELECT NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor",
                p => (Cores: Wmi.GetUInt(p, "NumberOfCores"), Logical: Wmi.GetUInt(p, "NumberOfLogicalProcessors"), Mhz: Wmi.GetUInt(p, "MaxClockSpeed")));
            if (processors.Count == 0)
            {
                return (null, null, null);
            }

            int? cores = processors.All(p => p.Cores is not null) ? processors.Sum(p => (int)p.Cores!.Value) : null;
            int? logical = processors.All(p => p.Logical is not null) ? processors.Sum(p => (int)p.Logical!.Value) : null;
            int? mhz = processors.Select(p => p.Mhz).Max() is { } highest ? (int)highest : null;
            return (cores, logical, mhz);
        }
        catch (Exception ex) when (ex is System.Management.ManagementException or COMException or UnauthorizedAccessException)
        {
            issues.Add("cpu-cores", ex);
            return (null, null, null);
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static int? Positive(int? value) => value is > 0 ? value : null;
}
