// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Boot.Dos;
using Bootrix.Core.Errors;

namespace Bootrix.Core.Tests.Writing.Dos;

public class DiskcopyDllTests
{
    [Fact]
    public void FindFloppyImage_ReturnsTheBinfileResourceByteForByte()
    {
        var floppy = MsDosFixtures.Floppy();

        var found = DiskcopyDll.FindFloppyImage(MsDosFixtures.Dll(floppy));

        Assert.Equal(floppy, found.ToArray());
    }

    [Fact]
    public void FindFloppyImage_IgnoresAResourceOfAnotherType()
    {
        var dll = MsDosFixtures.Dll(MsDosFixtures.Floppy(), typeName: "OTHERFIL");

        Assert.Equal(ErrorCode.MsDosImageInvalid, Assert.Throws<BootrixException>(() => DiskcopyDll.FindFloppyImage(dll)).Code);
    }

    [Fact]
    public void FindFloppyImage_RefusesAResourceOfTheWrongSize()
    {
        var dll = MsDosFixtures.Dll(MsDosFixtures.Floppy()[..737_280]);

        Assert.Equal(ErrorCode.MsDosImageInvalid, Assert.Throws<BootrixException>(() => DiskcopyDll.FindFloppyImage(dll)).Code);
    }

    [Theory]
    [InlineData(510, 0x00)]
    [InlineData(0x0C, 0x04)]
    [InlineData(0x13, 0x10)]
    [InlineData(0x15, 0xF8)]
    public void FindFloppyImage_RefusesDataThatIsNoFloppyVolume(int at, int value)
    {
        var floppy = MsDosFixtures.Floppy();
        floppy[at] = (byte)value;

        Assert.Equal(ErrorCode.MsDosImageInvalid, Assert.Throws<BootrixException>(() => DiskcopyDll.FindFloppyImage(MsDosFixtures.Dll(floppy))).Code);
    }

    [Fact]
    public void FindFloppyImage_RefusesAResourceThatReachesPastTheEndOfTheFile()
    {
        var dll = MsDosFixtures.Dll(MsDosFixtures.Floppy(), declaredSize: 0x7FFF_0000);

        Assert.Equal(ErrorCode.MsDosImageInvalid, Assert.Throws<BootrixException>(() => DiskcopyDll.FindFloppyImage(dll)).Code);
    }

    [Fact]
    public void FindFloppyImage_RefusesAFileThatIsNoPe()
    {
        var ex = Assert.Throws<BootrixException>(() => DiskcopyDll.FindFloppyImage(new byte[4096]));

        Assert.Equal(ErrorCode.MsDosImageInvalid, ex.Code);
    }

    [Fact]
    public void FindFloppyImage_RefusesAPeWithoutResources()
    {
        var pe = Bootrix.Core.Tests.Boot.PeBuilder.Typical().Build();

        Assert.Equal(ErrorCode.MsDosImageInvalid, Assert.Throws<BootrixException>(() => DiskcopyDll.FindFloppyImage(pe)).Code);
    }

    [Fact]
    public void FindFloppyImage_SurvivesATruncatedFile()
    {
        var dll = MsDosFixtures.Dll(MsDosFixtures.Floppy());

        foreach (var length in new[] { 0, 64, 300, 0x400, 0x1800, dll.Length / 2 })
        {
            Assert.Equal(ErrorCode.MsDosImageInvalid, Assert.Throws<BootrixException>(() => DiskcopyDll.FindFloppyImage(dll[..length])).Code);
        }
    }

    [Fact]
    public void FindFloppyImage_SurvivesDamagedResourceDirectories()
    {
        var dll = MsDosFixtures.Dll(MsDosFixtures.Floppy());
        var random = new Random(5);

        // The resource tree starts at the file offset of the .rsrc section; garbage in it must end in the documented error, never in a crash.
        for (var round = 0; round < 200; round++)
        {
            var damaged = (byte[])dll.Clone();
            for (var i = 0; i < 6; i++)
            {
                damaged[0x800 + random.Next(120)] = (byte)random.Next(256);
            }

            try
            {
                _ = DiskcopyDll.FindFloppyImage(damaged);
            }
            catch (BootrixException ex)
            {
                Assert.Equal(ErrorCode.MsDosImageInvalid, ex.Code);
            }
        }
    }
}
