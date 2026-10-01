// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;
using Arm = System.Runtime.Intrinsics.Arm;
using X86 = System.Runtime.Intrinsics.X86;

namespace Bootrix.Core.Workshop.Hardware;

public sealed record CpuProbe
{
    public CpuFeatures Features { get; init; }

    public string? Vendor { get; init; }

    public string? BrandString { get; init; }

    public bool? Is64BitCapable { get; init; }

    public bool? HypervisorPresent { get; init; }

    public string? HypervisorVendor { get; init; }
}

/// <summary>
/// Reads the instruction sets through the runtime's intrinsics and CPUID. It lives in the platform-neutral
/// core because it needs no operating system API, which makes it testable against /proc/cpuinfo.
/// </summary>
public static class CpuFeatureProbe
{
    private const int HypervisorBit = 31;
    private const int LongModeBit = 29;
    private const int ExtendedLeafBase = unchecked((int)0x80000000);
    private const int HypervisorLeafBase = 0x40000000;

    public static CpuProbe Detect()
    {
        if (X86.X86Base.IsSupported)
        {
            return DetectX86();
        }

        if (Arm.ArmBase.IsSupported)
        {
            return new CpuProbe { Features = DetectArm(), Is64BitCapable = true };
        }

        return new CpuProbe();
    }

    private static CpuProbe DetectX86()
    {
        var features = CpuFeatures.None;
        Add(ref features, X86.Sse2.IsSupported, CpuFeatures.Sse2);
        Add(ref features, X86.Sse3.IsSupported, CpuFeatures.Sse3);
        Add(ref features, X86.Ssse3.IsSupported, CpuFeatures.Ssse3);
        Add(ref features, X86.Sse41.IsSupported, CpuFeatures.Sse41);
        Add(ref features, X86.Sse42.IsSupported, CpuFeatures.Sse42);
        Add(ref features, X86.Popcnt.IsSupported, CpuFeatures.Popcnt);
        Add(ref features, X86.Avx.IsSupported, CpuFeatures.Avx);
        Add(ref features, X86.Avx2.IsSupported, CpuFeatures.Avx2);
        Add(ref features, X86.Bmi1.IsSupported, CpuFeatures.Bmi1);
        Add(ref features, X86.Bmi2.IsSupported, CpuFeatures.Bmi2);
        Add(ref features, X86.Fma.IsSupported, CpuFeatures.Fma);
        Add(ref features, X86.Lzcnt.IsSupported, CpuFeatures.Lzcnt);
        Add(ref features, X86.Aes.IsSupported, CpuFeatures.Aes);
        Add(ref features, X86.Pclmulqdq.IsSupported, CpuFeatures.Pclmulqdq);
        Add(ref features, X86.Avx512F.IsSupported, CpuFeatures.Avx512F);

        var leaf0 = X86.X86Base.CpuId(0, 0);
        var maxLeaf = leaf0.Eax;
        var vendor = AsciiFrom(leaf0.Ebx, leaf0.Edx, leaf0.Ecx);

        var maxExtended = (uint)X86.X86Base.CpuId(ExtendedLeafBase, 0).Eax;
        // A CPU without extended leaves predates AMD64 and cannot have long mode.
        var is64Bit = maxExtended >= 0x80000001 && (X86.X86Base.CpuId(ExtendedLeafBase + 1, 0).Edx & (1 << LongModeBit)) != 0;

        string? brand = null;
        if (maxExtended >= 0x80000004)
        {
            var parts = new int[12];
            for (var i = 0; i < 3; i++)
            {
                var r = X86.X86Base.CpuId(ExtendedLeafBase + 2 + i, 0);
                parts[i * 4] = r.Eax;
                parts[(i * 4) + 1] = r.Ebx;
                parts[(i * 4) + 2] = r.Ecx;
                parts[(i * 4) + 3] = r.Edx;
            }

            brand = AsciiFrom(parts);
        }

        var hypervisor = maxLeaf >= 1 && (X86.X86Base.CpuId(1, 0).Ecx & (1 << HypervisorBit)) != 0;
        string? hypervisorVendor = null;
        if (hypervisor)
        {
            // Leaf 0x40000000 is only defined when the hypervisor bit is set.
            var h = X86.X86Base.CpuId(HypervisorLeafBase, 0);
            hypervisorVendor = AsciiFrom(h.Ebx, h.Ecx, h.Edx);
        }

        return new CpuProbe
        {
            Features = features,
            Vendor = vendor,
            BrandString = brand,
            Is64BitCapable = is64Bit,
            HypervisorPresent = hypervisor,
            HypervisorVendor = hypervisorVendor,
        };
    }

    private static CpuFeatures DetectArm()
    {
        var features = CpuFeatures.None;
        Add(ref features, Arm.AdvSimd.IsSupported, CpuFeatures.ArmAdvSimd);
        Add(ref features, Arm.Aes.IsSupported, CpuFeatures.ArmAes);
        Add(ref features, Arm.Crc32.IsSupported, CpuFeatures.ArmCrc32);
        Add(ref features, Arm.Dp.IsSupported, CpuFeatures.ArmDp);
        Add(ref features, Arm.Rdm.IsSupported, CpuFeatures.ArmRdm);
        Add(ref features, Arm.Sha1.IsSupported, CpuFeatures.ArmSha1);
        Add(ref features, Arm.Sha256.IsSupported, CpuFeatures.ArmSha256);
        return features;
    }

    private static void Add(ref CpuFeatures features, bool present, CpuFeatures flag)
    {
        if (present)
        {
            features |= flag;
        }
    }

    private static string? AsciiFrom(params int[] registers)
    {
        var bytes = new byte[registers.Length * 4];
        for (var i = 0; i < registers.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4), registers[i]);
        }

        // Brand strings are NUL padded and, on older CPUs, also space padded on the left.
        var text = Encoding.ASCII.GetString(bytes).Trim('\0', ' ');
        return text.Length == 0 ? null : text;
    }
}
