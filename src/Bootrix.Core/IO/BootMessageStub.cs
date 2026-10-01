// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;

namespace Bootrix.Core.IO;

/// <summary>
/// The code that goes into a boot sector or MBR when nothing bootable is installed: print a
/// message through the BIOS and stop. Hand-assembled real-mode code that finds its message with a
/// call/pop so it runs the same at 0000:7C00 and 07C0:0000 and at any offset inside the sector.
/// </summary>
internal static class BootMessageStub
{
    public const string DiskMessage = "This disk is not bootable. Insert a system disk and press any key";
    public const string MbrMessage = "Missing operating system";

    // cli; xor ax,ax; mov ss,ax; mov sp,7C00h; sti; call $+3; pop si; add si,<message>; push cs; pop ds
    // next: lodsb; test al,al; jz tail; mov ah,0Eh; mov bx,7; int 10h; jmp next
    private static ReadOnlySpan<byte> Prefix =>
    [
        0xFA, 0x31, 0xC0, 0x8E, 0xD0, 0xBC, 0x00, 0x7C, 0xFB, 0xE8, 0x00, 0x00, 0x5E, 0x83, 0xC6, 0x00,
        0x0E, 0x1F, 0xAC, 0x84, 0xC0, 0x74, 0x09, 0xB4, 0x0E, 0xBB, 0x07, 0x00, 0xCD, 0x10, 0xEB, 0xF2,
    ];

    // xor ax,ax; int 16h; int 19h: wait for a key, then let the BIOS try the next boot device
    private static ReadOnlySpan<byte> WaitForKey => [0x31, 0xC0, 0xCD, 0x16, 0xCD, 0x19];

    // hlt; jmp $-1
    private static ReadOnlySpan<byte> Halt => [0xF4, 0xEB, 0xFD];

    private const int CallReturnOffset = 12;
    private const int DisplacementIndex = 15;

    /// <summary>Stub for the code area of a FAT boot sector (at least 420 bytes are available).</summary>
    public static byte[] ForFileSystem() => Create(DiskMessage, WaitForKey);

    /// <summary>Stub for the 440-byte bootstrap area of an MBR.</summary>
    public static byte[] ForMbr() => Create(MbrMessage, Halt);

    private static byte[] Create(string message, ReadOnlySpan<byte> tail)
    {
        var text = Encoding.ASCII.GetBytes("\r\n" + message + "\r\n");
        var code = new byte[Prefix.Length + tail.Length + text.Length + 1];
        Prefix.CopyTo(code);
        tail.CopyTo(code.AsSpan(Prefix.Length));
        text.CopyTo(code, Prefix.Length + tail.Length);
        code[DisplacementIndex] = (byte)(Prefix.Length + tail.Length - CallReturnOffset);
        return code;
    }
}
