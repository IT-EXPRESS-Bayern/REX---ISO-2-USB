// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images;

namespace Bootrix.Core.Tests.Images;

public sealed class BootFileProbeTests
{
    private static ImageFileIndex Tree(params string[] files)
    {
        var index = new ImageFileIndex();
        foreach (var file in files)
        {
            index.AddFile(file, 1);
        }

        return index;
    }

    [Theory]
    [InlineData("isolinux/isolinux.bin")]
    [InlineData("boot/syslinux/syslinux.cfg")]
    [InlineData("extlinux.conf")]
    [InlineData("boot/grub/i386-pc/normal.mod")]
    [InlineData("bootmgr")]
    [InlineData("BOOTMGR.")]
    [InlineData("boot/bcd")]
    [InlineData("NTLDR")]
    [InlineData("I386/SETUPLDR.BIN")]
    [InlineData("grldr")]
    [InlineData("kolibri.img")]
    [InlineData("loader/setupldr.sys")]
    public void HasBiosBootFiles_RecognisesTheLoadersOfTheCommonFamilies(string file) =>
        Assert.True(BootFileProbe.HasBiosBootFiles([Tree(file, "readme.txt")]));

    [Fact]
    public void HasBiosBootFiles_IsFalseForUefiOnlyMedia() =>
        Assert.False(BootFileProbe.HasBiosBootFiles([Tree("EFI/BOOT/BOOTX64.EFI", "boot/grub/x86_64-efi/normal.mod", "casper/vmlinuz")]));

    [Fact]
    public void HasBiosBootFiles_LooksIntoEveryTree() =>
        Assert.True(BootFileProbe.HasBiosBootFiles([Tree("a"), Tree("isolinux/isolinux.bin")]));

    [Fact]
    public void EfiLoaders_AreFoundBelowEfiWhateverTheDirectory()
    {
        var trees = new[]
        {
            Tree("EFI/BOOT/BOOTX64.EFI;1", "EFI/fedora/shimx64.efi", "EFI/BOOT/random.efi", "EFI/BOOT/grub.cfg", "boot/bootx64.efi"),
        };

        Assert.Equal(
            ["EFI/BOOT/BOOTX64.EFI", "EFI/fedora/shimx64.efi"],
            BootFileProbe.EfiLoaders(trees).Order(StringComparer.Ordinal));
        Assert.True(BootFileProbe.HasEfiBootFiles(trees));
    }

    [Fact]
    public void HasEfiBootFiles_IsFalseWhenTheLoaderLiesOutsideEfi() =>
        Assert.False(BootFileProbe.HasEfiBootFiles([Tree("boot/bootx64.efi", "EFI/BOOT/readme.txt")]));

    [Fact]
    public void EfiArchitectures_AreMappedDistinctAndOrdered()
    {
        var trees = new[]
        {
            Tree("efi/boot/bootaa64.efi", "efi/boot/bootx64.efi", "efi/boot/bootia32.efi", "efi/boot/bootarm.efi", "efi/fedora/shimx64.efi", "efi/boot/grubx64.efi"),
        };

        Assert.Equal([WindowsArch.X86, WindowsArch.X64, WindowsArch.Arm64, WindowsArch.Arm], BootFileProbe.EfiArchitectures(trees));
    }

    [Fact]
    public void EfiArchitectures_IgnoreLoadersWithoutAKnownArchitecture()
    {
        var trees = new[] { Tree("efi/boot/grub.efi", "efi/boot/bootia64.efi", "efi/boot/bootriscv64.efi") };

        Assert.Empty(BootFileProbe.EfiArchitectures(trees));
        Assert.True(BootFileProbe.HasEfiBootFiles(trees));
    }

    [Fact]
    public void EfiArchitectures_CombineSeveralTrees() =>
        Assert.Equal([WindowsArch.X86, WindowsArch.X64], BootFileProbe.EfiArchitectures([Tree("efi/boot/bootx64.efi"), Tree("efi/boot/bootia32.efi")]));
}
