// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Boot;

public enum EfiMachine
{
    Unknown,
    X86,
    X64,
    Arm,
    Arm64,
    RiscV64,
    LoongArch64,
}

public static class EfiMachineInfo
{
    /// <summary>Maps the COFF Machine field using the image machine types of UEFI 2.x (section 2.3).</summary>
    public static EfiMachine FromPeMachine(ushort machine) => machine switch
    {
        0x014C => EfiMachine.X86,
        0x8664 => EfiMachine.X64,
        0x01C2 or 0x01C4 => EfiMachine.Arm, // Thumb and Thumb-2; UEFI does not use plain 0x01C0
        0xAA64 => EfiMachine.Arm64,
        0x5064 => EfiMachine.RiscV64,
        0x6264 => EfiMachine.LoongArch64,
        _ => EfiMachine.Unknown,
    };

    /// <summary>The architecture part of the removable-media fallback name, for example "X64" in BOOTX64.EFI.</summary>
    public static string? FallbackSuffix(EfiMachine machine) => machine switch
    {
        EfiMachine.X86 => "IA32",
        EfiMachine.X64 => "X64",
        EfiMachine.Arm => "ARM",
        EfiMachine.Arm64 => "AA64",
        EfiMachine.RiscV64 => "RISCV64",
        EfiMachine.LoongArch64 => "LOONGARCH64",
        _ => null,
    };
}
