// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Bootrix.Core.Errors;

namespace Bootrix.Core.Boot.Dos;

/// <summary>The FreeDOS 1.4 base system: kernel, command interpreter and a small configuration.</summary>
public static class FreeDosSystem
{
    // The kernel keeps a configuration block behind its first jump (kernel/kernel.asm, "KERNEL CONFIGURATION AREA"):
    // 'CONFIG', a size word, then DLASORT, SHOWDRIVEASSIGNMENT, SKIPCONFIGSECONDS and FORCELBA. SYS CONFIG edits the same bytes.
    private const int ConfigSignatureOffset = 2;
    private const int ConfigStartOffset = 10;
    private const int ForceLbaOffset = 0x0D;

    /// <summary>
    /// <paramref name="floppy"/> picks the 8086 kernel unless one is asked for; <paramref name="forceLba"/> makes the kernel
    /// address partitions by LBA even when their type byte says CHS, which sidesteps BIOSes with a wrong geometry.
    /// </summary>
    public static DosSystem Create(DosKernel kernel = DosKernel.Auto, bool floppy = false, bool forceLba = true)
    {
        var i386 = kernel switch
        {
            DosKernel.I386 => true,
            DosKernel.I8086 => false,
            _ => !floppy,
        };

        var kernelImage = DosAssets.FreeDos(i386 ? "KERNL386.SYS" : "KERNL86.SYS");
        if (forceLba && !floppy)
        {
            kernelImage = WithForcedLba(kernelImage);
        }

        IReadOnlyList<DosFile> files =
        [
            new("\\KERNEL.SYS", kernelImage, DosFile.SystemFile),
            new("\\COMMAND.COM", DosAssets.FreeDos("COMMAND.COM"), DosFile.SystemFile),
            DosFile.Text("\\FDCONFIG.SYS", "FILES=40\nBUFFERS=20\nLASTDRIVE=Z\n"),
            DosFile.Text("\\AUTOEXEC.BAT", "@ECHO OFF\nSET PATH=.;\\\nECHO FreeDOS 1.4 (kernel 2043, FreeCOM 0.86a)\n"),
        ];

        return new DosSystem(DosFlavor.FreeDos, files, (options, _) => FreeDosBootSector.Apply(options));
    }

    internal static byte[] WithForcedLba(byte[] kernel)
    {
        if (kernel.Length <= ForceLbaOffset || !kernel.AsSpan(ConfigSignatureOffset, 6).SequenceEqual("CONFIG"u8))
        {
            throw new InvalidDataException("The FreeDOS kernel has no configuration area.");
        }

        var size = BinaryPrimitives.ReadUInt16LittleEndian(kernel.AsSpan(ConfigSignatureOffset + 6));
        if (size <= ForceLbaOffset - ConfigStartOffset)
        {
            throw new InvalidDataException("The FreeDOS kernel configuration area is too small.");
        }

        var patched = (byte[])kernel.Clone();
        patched[ForceLbaOffset] = 1;
        return patched;
    }
}
