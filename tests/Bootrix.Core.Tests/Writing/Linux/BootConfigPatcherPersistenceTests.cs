// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Writing.Linux.Patching;

namespace Bootrix.Core.Tests.Writing.Linux;

public class BootConfigPatcherPersistenceTests
{
    private static ConfigPatchOptions Casper => new() { Persistence = PersistenceStyle.Casper };

    private static ConfigPatchOptions LiveBoot => new() { Persistence = PersistenceStyle.LiveBoot };

    [Theory]
    [InlineData(
        "  append  file=/cdrom/preseed/ubuntu.seed boot=casper initrd=/casper/initrd quiet splash ---",
        "  append  file=/cdrom/preseed/ubuntu.seed boot=casper persistent initrd=/casper/initrd quiet splash ---")]
    [InlineData(
        "append initrd=/casper/initrd.lz boot=casper quiet splash --",
        "append initrd=/casper/initrd.lz boot=casper persistent quiet splash --")]
    [InlineData(
        "\tlinux\t/casper/vmlinuz  quiet splash ---",
        "\tlinux\t/casper/vmlinuz persistent  quiet splash ---")]
    [InlineData(
        "linux /casper/vmlinuz.efi --- quiet splash",
        "linux /casper/vmlinuz.efi persistent --- quiet splash")]
    [InlineData(
        "linuxefi /casper/vmlinuz boot=casper quiet",
        "linuxefi /casper/vmlinuz boot=casper persistent quiet")]
    [InlineData(
        "APPEND BOOT=casper x",
        "APPEND BOOT=casper x")]
    public void Casper_AddsPersistentWhereTheLiveSystemIsNamed(string line, string expected)
    {
        Assert.Equal(expected, BootConfigPatcher.Patch(line, Casper).Text);
    }

    [Fact]
    public void Casper_PreseedOnlyLine_PutsPersistentInFrontOfTheSeedAndDropsMaybeUbiquity()
    {
        var result = BootConfigPatcher.Patch("linux /boot/vmlinuz file=/cdrom/preseed/ubuntu.seed maybe-ubiquity quiet splash ---", Casper);

        Assert.Equal("linux /boot/vmlinuz persistent file=/cdrom/preseed/ubuntu.seed quiet splash ---", result.Text);
        Assert.Equal([ConfigChangeKind.RemovedParameter, ConfigChangeKind.PersistenceParameter], result.Changes.Select(c => c.Kind));
    }

    [Fact]
    public void Casper_GrubEntryWithKernelPathAndMaybeUbiquity_GetsBoth()
    {
        var result = BootConfigPatcher.Patch("linux /casper/vmlinuz file=/cdrom/preseed/ubuntu.seed maybe-ubiquity quiet splash ---", Casper);

        Assert.Equal("linux /casper/vmlinuz persistent file=/cdrom/preseed/ubuntu.seed quiet splash ---", result.Text);
    }

    [Fact]
    public void Casper_ExistingParameter_LeavesTheLineAlone()
    {
        var line = "append boot=casper persistent quiet";

        var result = BootConfigPatcher.Patch(line, Casper);

        Assert.False(result.Changed);
        Assert.Same(line, result.Text);
    }

    [Theory]
    [InlineData("append initrd=/live/initrd.img boot=live components quiet splash", "append initrd=/live/initrd.img boot=live persistence components quiet splash")]
    [InlineData("  linux /live/vmlinuz-6.1.0-18-amd64 boot=live components quiet splash findiso=${iso_path}", "  linux /live/vmlinuz-6.1.0-18-amd64 boot=live persistence components quiet splash findiso=${iso_path}")]
    [InlineData("append initrd=/live/initrd.img boot=live noconfig=sudo persistence", "append initrd=/live/initrd.img boot=live noconfig=sudo persistence")]
    [InlineData("append boot=live persistence-label=casper-rw", "append boot=live persistence persistence-label=casper-rw")]
    public void LiveBoot_AddsPersistenceAfterBootLive(string line, string expected)
    {
        Assert.Equal(expected, BootConfigPatcher.Patch(line, LiveBoot).Text);
    }

    [Theory]
    [InlineData("linux16 /boot/memtest.bin")]
    [InlineData("append initrd=/live/initrd.img quiet")]
    [InlineData("kernel /casper/vmlinuz")]
    [InlineData("menuentry \"Try Ubuntu\" { linux /casper/vmlinuz quiet }")]
    [InlineData("initrd /casper/initrd boot=casper")]
    [InlineData("# append boot=casper quiet")]
    [InlineData("label live")]
    [InlineData("")]
    public void Persistence_LinesWithoutALiveBootCommandLine_AreNotTouched(string line)
    {
        Assert.False(BootConfigPatcher.Patch(line, Casper).Changed);
        Assert.False(BootConfigPatcher.Patch(line, LiveBoot).Changed);
    }

    [Fact]
    public void Persistence_Disabled_ChangesNothing()
    {
        var text = "append boot=casper quiet\nappend boot=live quiet\n";

        Assert.False(BootConfigPatcher.Patch(text, new ConfigPatchOptions()).Changed);
    }

    [Fact]
    public void Casper_DoesNotApplyLiveBootRule_AndTheOtherWayRound()
    {
        Assert.False(BootConfigPatcher.Patch("append boot=live quiet", Casper).Changed);
        Assert.False(BootConfigPatcher.Patch("append boot=casper quiet", LiveBoot).Changed);
    }

    [Fact]
    public void Patch_MultipleEntries_ChangesEveryOneAndReportsLineNumbers()
    {
        const string text =
            "default vesamenu.c32\n" +
            "label live\n" +
            "  menu label Live\n" +
            "  kernel /live/vmlinuz\n" +
            "  append initrd=/live/initrd.img boot=live quiet\n" +
            "label failsafe\n" +
            "  kernel /live/vmlinuz\n" +
            "  append initrd=/live/initrd.img boot=live noapic\n" +
            "label memtest\n" +
            "  kernel /live/memtest\n";

        var result = BootConfigPatcher.Patch(text, LiveBoot);

        Assert.Equal([5, 8], result.Changes.Select(c => c.Line));
        Assert.Contains("boot=live persistence quiet\n", result.Text, StringComparison.Ordinal);
        Assert.Contains("boot=live persistence noapic\n", result.Text, StringComparison.Ordinal);
        Assert.EndsWith("  kernel /live/memtest\n", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Patch_CrLfLineEndings_AreKept()
    {
        const string text = "label live\r\n  append boot=live quiet\r\nlabel other\r\n  append quiet\r\n";

        var result = BootConfigPatcher.Patch(text, LiveBoot);

        Assert.Equal("label live\r\n  append boot=live persistence quiet\r\nlabel other\r\n  append quiet\r\n", result.Text);
    }

    [Fact]
    public void Patch_MixedLineEndingsAndMissingFinalNewline_AreKept()
    {
        const string text = "append boot=live a\nappend boot=live b\r\nappend boot=live c";

        var result = BootConfigPatcher.Patch(text, LiveBoot);

        Assert.Equal("append boot=live persistence a\nappend boot=live persistence b\r\nappend boot=live persistence c", result.Text);
    }

    [Fact]
    public void Patch_NoMatch_ReturnsTheSameInstance()
    {
        var text = "default menu\r\nprompt 0\r\n";

        Assert.Same(text, BootConfigPatcher.Patch(text, LiveBoot).Text);
    }

    [Fact]
    public void Patch_BytesAbove0x7F_SurviveBecauseTheTextIsLatin1()
    {
        var bytes = new byte[] { 0x6D, 0x65, 0x6E, 0x75, 0x20, 0xC3, 0xA4, 0x0A, 0x61, 0x70, 0x70, 0x65, 0x6E, 0x64, 0x20, 0x62, 0x6F, 0x6F, 0x74, 0x3D, 0x6C, 0x69, 0x76, 0x65, 0x0A };
        var text = System.Text.Encoding.Latin1.GetString(bytes);

        var result = BootConfigPatcher.Patch(text, LiveBoot);

        var roundTrip = System.Text.Encoding.Latin1.GetBytes(result.Text);
        Assert.Equal(bytes.Take(8), roundTrip.Take(8));
        Assert.Equal("menu Ã¤\nappend boot=live persistence\n", result.Text);
    }

    [Theory]
    [MemberData(nameof(RealisticConfigs))]
    public void Patch_Twice_ChangesNothingTheSecondTime(string text, PersistenceStyle style)
    {
        var options = new ConfigPatchOptions { Persistence = style, OldLabel = "OLD_LABEL", NewLabel = "NEW" };

        var first = BootConfigPatcher.Patch(text, options);
        var second = BootConfigPatcher.Patch(first.Text, options);

        Assert.False(second.Changed);
        Assert.Equal(first.Text, second.Text);
    }

    public static TheoryData<string, PersistenceStyle> RealisticConfigs => new()
    {
        { "label l\n  append file=/cdrom/preseed/ubuntu.seed boot=casper initrd=/casper/initrd quiet splash ---\n  append root=live:CDLABEL=OLD_LABEL\n", PersistenceStyle.Casper },
        { "menuentry 'x' {\n\tlinux /casper/vmlinuz file=/cdrom/preseed/ubuntu.seed maybe-ubiquity quiet splash ---\n\tinitrd /casper/initrd\n}\n", PersistenceStyle.Casper },
        { "label l\n  append initrd=/live/initrd.img boot=live components quiet splash archisolabel=OLD_LABEL\n", PersistenceStyle.LiveBoot },
    };

    [Theory]
    [InlineData("append -c boot.cfg", "append -c boot.cfg -p 1")]
    [InlineData("  APPEND -c boot.cfg", "  APPEND -c boot.cfg -p 1")]
    [InlineData("append -c boot.cfg -p 1", "append -c boot.cfg -p 1")]
    [InlineData("append -c boot.cfg runweasel", "append -c boot.cfg -p 1 runweasel")]
    [InlineData("append quiet", "append quiet")]
    public void Esxi_BootCfgLines_GetTheFirstPartition(string line, string expected)
    {
        var result = BootConfigPatcher.Patch(line, new ConfigPatchOptions { EsxiFirstPartition = true });

        Assert.Equal(expected, result.Text);
    }

    [Fact]
    public void Esxi_Disabled_LeavesBootCfgAlone()
    {
        Assert.False(BootConfigPatcher.Patch("append -c boot.cfg", new ConfigPatchOptions()).Changed);
    }
}
