// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Workshop.Hardware;

public enum CpuArchitecture
{
    Unknown,
    X86,
    X64,
    Arm,
    Arm64,
}

/// <summary>
/// Instruction set extensions that matter for choosing a Windows version. The set is measured
/// inside the running process, so it describes the hardware only when the process is not emulated.
/// </summary>
[Flags]
public enum CpuFeatures : long
{
    None = 0,
    Sse2 = 1L << 0,
    Sse3 = 1L << 1,
    Ssse3 = 1L << 2,
    Sse41 = 1L << 3,
    Sse42 = 1L << 4,
    Popcnt = 1L << 5,
    Avx = 1L << 6,
    Avx2 = 1L << 7,
    Bmi1 = 1L << 8,
    Bmi2 = 1L << 9,
    Fma = 1L << 10,
    Lzcnt = 1L << 11,
    Aes = 1L << 12,
    Pclmulqdq = 1L << 13,
    Avx512F = 1L << 14,

    ArmAdvSimd = 1L << 32,
    ArmAes = 1L << 33,
    ArmCrc32 = 1L << 34,
    ArmDp = 1L << 35,
    ArmRdm = 1L << 36,
    ArmSha1 = 1L << 37,
    ArmSha256 = 1L << 38,
}
