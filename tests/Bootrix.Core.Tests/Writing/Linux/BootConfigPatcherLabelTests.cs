// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Writing.Linux.Patching;

namespace Bootrix.Core.Tests.Writing.Linux;

public class BootConfigPatcherLabelTests
{
    private static ConfigPatchResult Patch(string text, string oldLabel, string newLabel) =>
        BootConfigPatcher.Patch(text, new ConfigPatchOptions { OldLabel = oldLabel, NewLabel = newLabel });

    [Theory]
    [InlineData(
        "\tlinux /images/pxeboot/vmlinuz root=live:CDLABEL=Fedora-WS-Live-39-1-5 rd.live.image quiet",
        "Fedora-WS-Live-39-1-5", "FEDORA-WS-L",
        "\tlinux /images/pxeboot/vmlinuz root=live:CDLABEL=FEDORA-WS-L rd.live.image quiet")]
    [InlineData(
        "  linuxefi /images/pxeboot/vmlinuz inst.stage2=hd:LABEL=Fedora-S-dvd-x86_64-39 quiet",
        "Fedora-S-dvd-x86_64-39", "FEDORA-S-DV",
        "  linuxefi /images/pxeboot/vmlinuz inst.stage2=hd:LABEL=FEDORA-S-DV quiet")]
    [InlineData(
        "search --no-floppy --set=root --label ARCH_202401",
        "ARCH_202401", "ARCH_202401X",
        "search --no-floppy --set=root --label ARCH_202401X")]
    [InlineData(
        "search --no-floppy --set=root --label \"ARCH_202401\"",
        "ARCH_202401", "STICK",
        "search --no-floppy --set=root --label \"STICK\"")]
    [InlineData(
        "options archisobasedir=arch archisolabel=ARCH_202401",
        "ARCH_202401", "STICK",
        "options archisobasedir=arch archisolabel=STICK")]
    [InlineData(
        "APPEND archisobasedir=arch archisolabel=ARCH_202401 cow_spacesize=1G",
        "ARCH_202401", "STICK",
        "APPEND archisobasedir=arch archisolabel=STICK cow_spacesize=1G")]
    [InlineData(
        "append root=/dev/disk/by-label/OLD ro",
        "OLD", "NEW",
        "append root=/dev/disk/by-label/NEW ro")]
    [InlineData(
        "append root=LABEL=OLD ro",
        "OLD", "NEW",
        "append root=LABEL=NEW ro")]
    public void Rewrite_LabelInKnownPlaces_FollowsTheMediumLabel(string line, string oldLabel, string newLabel, string expected)
    {
        var result = Patch(line, oldLabel, newLabel);

        Assert.Equal(expected, result.Text);
        Assert.Equal(ConfigChangeKind.Label, Assert.Single(result.Changes).Kind);
    }

    [Theory]
    [InlineData("append hostname=Debian quiet", "Debian")]
    [InlineData("append root=live:CDLABEL=Debian-live quiet", "Debian")]
    [InlineData("append root=live:CDLABEL=Debian quiet", "Debian-live")]
    [InlineData("append root=live:CDLABEL=XDebian quiet", "Debian")]
    [InlineData("menuentry \"Debian\" { }", "Debian")]
    [InlineData("append root=live:CDLABEL=debian quiet", "Debian")]
    [InlineData("default Debian", "Debian")]
    public void Rewrite_LabelOutsideAKeyOrLongerThanTheLabel_IsLeftAlone(string line, string label)
    {
        Assert.False(Patch(line, label, "NEWLABEL").Changed);
    }

    [Fact]
    public void Rewrite_LabelWithBlanks_IsFoundEscapedAndQuoted()
    {
        const string oldLabel = "Ubuntu 22.04 LTS amd64";
        const string newLabel = "UBUNTU 22.0";
        const string text =
            "linux /casper/vmlinuz root=live:CDLABEL=Ubuntu\\x2022.04\\x20LTS\\x20amd64 quiet\n" +
            "search --label \"Ubuntu 22.04 LTS amd64\" --set=root\n";

        var result = Patch(text, oldLabel, newLabel);

        Assert.Equal(
            "linux /casper/vmlinuz root=live:CDLABEL=UBUNTU\\x2022.0 quiet\n" +
            "search --label \"UBUNTU 22.0\" --set=root\n",
            result.Text);
        Assert.Equal(2, result.Changes.Count);
    }

    [Fact]
    public void Rewrite_NewLabelWithBlankWhereTheOldOneHadNone_IsEscapedForTheCommandLine()
    {
        var result = Patch("append root=live:CDLABEL=LIVE quiet", "LIVE", "MY STICK");

        Assert.Equal("append root=live:CDLABEL=MY\\x20STICK quiet", result.Text);
    }

    [Fact]
    public void Rewrite_SlashInTheLabel_IsFoundAsX2f()
    {
        var result = Patch("append root=live:CDLABEL=A\\x2fB quiet", "A/B", "AB");

        Assert.Equal("append root=live:CDLABEL=AB quiet", result.Text);
    }

    [Fact]
    public void Rewrite_SpecialRegexCharactersInTheLabel_AreTakenLiterally()
    {
        var result = Patch("append root=live:CDLABEL=a.b+c(d) quiet", "a.b+c(d)", "X");

        Assert.Equal("append root=live:CDLABEL=X quiet", result.Text);
        Assert.False(Patch("append root=live:CDLABEL=aXb+c(d) quiet", "a.b+c(d)", "X").Changed);
    }

    [Fact]
    public void Rewrite_EveryOccurrenceInALine_IsReplaced()
    {
        var result = Patch("append root=live:CDLABEL=OLD rd.live.overlay=LABEL=OLD", "OLD", "NEW");

        Assert.Equal("append root=live:CDLABEL=NEW rd.live.overlay=LABEL=NEW", result.Text);
        Assert.Single(result.Changes);
    }

    [Theory]
    [InlineData(null, "NEW")]
    [InlineData("OLD", null)]
    [InlineData("", "NEW")]
    [InlineData("OLD", "OLD")]
    public void Rewrite_WithoutTwoDifferentLabels_DoesNothing(string? oldLabel, string? newLabel)
    {
        var result = BootConfigPatcher.Patch("append root=live:CDLABEL=OLD", new ConfigPatchOptions { OldLabel = oldLabel, NewLabel = newLabel });

        Assert.False(result.Changed);
    }

    [Fact]
    public void Rewrite_KeepsLineEndingsAndOtherLines()
    {
        const string text = "default menu\r\nlabel a\r\n  append root=live:CDLABEL=OLD quiet\r\n\r\nlabel b\r\n  kernel memtest\r\n";

        var result = Patch(text, "OLD", "NEW");

        Assert.Equal("default menu\r\nlabel a\r\n  append root=live:CDLABEL=NEW quiet\r\n\r\nlabel b\r\n  kernel memtest\r\n", result.Text);
    }

    [Fact]
    public void Rewrite_AndPersistenceTogether_ReportBothChanges()
    {
        var options = new ConfigPatchOptions { OldLabel = "OLD", NewLabel = "NEW", Persistence = PersistenceStyle.LiveBoot };

        var result = BootConfigPatcher.Patch("append boot=live root=live:CDLABEL=OLD quiet", options);

        Assert.Equal("append boot=live persistence root=live:CDLABEL=NEW quiet", result.Text);
        Assert.Equal([ConfigChangeKind.Label, ConfigChangeKind.PersistenceParameter], result.Changes.Select(c => c.Kind));
    }
}

public class Md5SumFileTests
{
    private const string Old = "0123456789abcdef0123456789abcdef";
    private const string Other = "fedcba9876543210fedcba9876543210";
    private const string New = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

    [Fact]
    public void Update_ReplacesTheSumOfListedFilesOnly()
    {
        var text = $"{Old}  ./isolinux/isolinux.cfg\n{Other}  ./casper/filesystem.squashfs\n";

        var updated = Md5SumFile.Update(text, new Dictionary<string, string> { ["isolinux/isolinux.cfg"] = New });

        Assert.Equal($"{New.ToLowerInvariant()}  ./isolinux/isolinux.cfg\n{Other}  ./casper/filesystem.squashfs\n", updated);
    }

    [Fact]
    public void Update_KeepsBinaryMarkerCrLfAndPathSpelling()
    {
        var text = $"{Old} *./Boot/Grub/grub.cfg\r\n{Other}  other\r\n";

        var updated = Md5SumFile.Update(text, new Dictionary<string, string> { ["boot/grub/grub.cfg"] = New });

        Assert.Equal($"{New.ToLowerInvariant()} *./Boot/Grub/grub.cfg\r\n{Other}  other\r\n", updated);
    }

    [Fact]
    public void Update_PathWithoutDotSlash_IsMatched()
    {
        var text = $"{Old}  isolinux/txt.cfg\n";

        Assert.Equal($"{New.ToLowerInvariant()}  isolinux/txt.cfg\n", Md5SumFile.Update(text, new Dictionary<string, string> { ["./isolinux/txt.cfg"] = New }));
    }

    [Fact]
    public void Update_UnlistedFileOrNoSums_ReturnsTheListUnchanged()
    {
        var text = $"{Old}  ./a\n# comment\n";

        Assert.Equal(text, Md5SumFile.Update(text, new Dictionary<string, string> { ["b"] = New }));
        Assert.Equal(text, Md5SumFile.Update(text, new Dictionary<string, string>()));
    }

    [Theory]
    [InlineData("md5sum.txt", true)]
    [InlineData("MD5SUMS", true)]
    [InlineData("MD5SUMS.txt", false)]
    [InlineData("sub/md5sum.txt", false)]
    public void IsListName_RecognisesTheRootLists(string path, bool expected)
    {
        Assert.Equal(expected, Md5SumFile.IsListName(path));
    }
}
