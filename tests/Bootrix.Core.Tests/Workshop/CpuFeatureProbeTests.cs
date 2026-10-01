// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Bootrix.Core.Workshop.Hardware;

namespace Bootrix.Core.Tests.Workshop;

public class CpuFeatureProbeTests
{
    private const string CpuInfoPath = "/proc/cpuinfo";

    [Fact]
    public void Detect_FeaturesAreConsistentWithTheInstructionSetHierarchy()
    {
        var probe = CpuFeatureProbe.Detect();
        var f = probe.Features;

        // Each of these extends the one before it; a CPU cannot have SSE4.2 without SSE4.1.
        if (f.HasFlag(CpuFeatures.Sse42))
        {
            Assert.True(f.HasFlag(CpuFeatures.Sse41));
        }

        if (f.HasFlag(CpuFeatures.Sse41))
        {
            Assert.True(f.HasFlag(CpuFeatures.Ssse3));
        }

        if (f.HasFlag(CpuFeatures.Avx2))
        {
            Assert.True(f.HasFlag(CpuFeatures.Avx));
        }
    }

    [Fact]
    public void Detect_ReportsTheArchitectureFamilyOfTheProcess()
    {
        var probe = CpuFeatureProbe.Detect();

        switch (RuntimeInformation.ProcessArchitecture)
        {
            case Architecture.X64:
            case Architecture.X86:
                Assert.True(probe.Features.HasFlag(CpuFeatures.Sse2) || RuntimeInformation.ProcessArchitecture == Architecture.X86);
                Assert.False(probe.Features.HasFlag(CpuFeatures.ArmAdvSimd));
                Assert.NotNull(probe.Vendor);
                break;
            case Architecture.Arm64:
                Assert.True(probe.Features.HasFlag(CpuFeatures.ArmAdvSimd));
                Assert.True(probe.Is64BitCapable);
                break;
        }
    }

    [Fact]
    public void Detect_X64Process_IsSixtyFourBitCapable()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        Assert.True(CpuFeatureProbe.Detect().Is64BitCapable);
    }

    /// <summary>/proc/cpuinfo is the kernel's own view of CPUID and does not depend on the runtime's intrinsics.</summary>
    [Fact]
    public void Detect_AgreesWithProcCpuInfoOnLinux()
    {
        if (!File.Exists(CpuInfoPath) || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        var flags = File.ReadLines(CpuInfoPath)
            .Where(l => l.StartsWith("flags", StringComparison.Ordinal))
            .Select(l => l[(l.IndexOf(':', StringComparison.Ordinal) + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .FirstOrDefault();
        if (flags is null)
        {
            return;
        }

        var kernel = new HashSet<string>(flags);
        var probe = CpuFeatureProbe.Detect();

        Assert.Equal(kernel.Contains("popcnt"), probe.Features.HasFlag(CpuFeatures.Popcnt));
        Assert.Equal(kernel.Contains("sse4_2"), probe.Features.HasFlag(CpuFeatures.Sse42));
        Assert.Equal(kernel.Contains("sse4_1"), probe.Features.HasFlag(CpuFeatures.Sse41));
        Assert.Equal(kernel.Contains("ssse3"), probe.Features.HasFlag(CpuFeatures.Ssse3));
        Assert.Equal(kernel.Contains("avx"), probe.Features.HasFlag(CpuFeatures.Avx));
        Assert.Equal(kernel.Contains("avx2"), probe.Features.HasFlag(CpuFeatures.Avx2));
        Assert.Equal(kernel.Contains("aes"), probe.Features.HasFlag(CpuFeatures.Aes));
        Assert.Equal(kernel.Contains("lm"), probe.Is64BitCapable);
        Assert.Equal(kernel.Contains("hypervisor"), probe.HypervisorPresent);
    }

    [Fact]
    public void Detect_BrandStringMatchesProcCpuInfoOnLinux()
    {
        if (!File.Exists(CpuInfoPath) || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            return;
        }

        var kernelName = File.ReadLines(CpuInfoPath)
            .Where(l => l.StartsWith("model name", StringComparison.Ordinal))
            .Select(l => l[(l.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim())
            .FirstOrDefault();
        var vendor = File.ReadLines(CpuInfoPath)
            .Where(l => l.StartsWith("vendor_id", StringComparison.Ordinal))
            .Select(l => l[(l.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim())
            .FirstOrDefault();
        if (kernelName is null || vendor is null)
        {
            return;
        }

        var probe = CpuFeatureProbe.Detect();

        Assert.Equal(kernelName, probe.BrandString);
        Assert.Equal(vendor, probe.Vendor);
    }

    [Fact]
    public void Detect_HypervisorVendorIsOnlyReportedWithTheHypervisorBit()
    {
        var probe = CpuFeatureProbe.Detect();

        if (probe.HypervisorPresent != true)
        {
            Assert.Null(probe.HypervisorVendor);
        }
    }
}
