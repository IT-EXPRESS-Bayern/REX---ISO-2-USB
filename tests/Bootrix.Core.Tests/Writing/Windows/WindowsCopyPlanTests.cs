// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Writing.Windows;

namespace Bootrix.Core.Tests.Writing.Windows;

public class WindowsCopyPlanTests
{
    private const long Gib = 1L << 30;

    private static MediaSourceFile File(string path, long length = 1000) => new(path, length);

    private static IEnumerable<MediaSourceFile> TypicalSetupMedium(long installBytes = 5 * Gib) =>
    [
        File("efi/boot/bootx64.efi", 100),
        File("efi/microsoft/boot/cdboot.efi", 100),
        File("sources/install.wim", installBytes),
        File("sources/boot.wim", 600_000_000),
        File("bootmgr", 400_000),
        File("boot/bcd", 16_000),
        File("boot/boot.sdi", 3_000_000),
        File("bootmgr.efi", 1_500_000),
        File("setup.exe", 90_000),
        File("autorun.inf", 50),
        File("support/logging/setup.log", 10),
    ];

    private static string[] Order(WindowsCopyPlan plan) => [.. plan.Items.Select(item => item.Source.Path)];

    [Fact]
    public void Order_IsDeterministicAndGroupedByTier()
    {
        var plan = WindowsCopyPlan.Create(TypicalSetupMedium(2 * Gib), [], new WindowsCopyOptions());

        Assert.Equal(
            [
                "autorun.inf", "setup.exe", "sources/boot.wim", "support/logging/setup.log",
                "sources/install.wim",
                "boot/bcd", "boot/boot.sdi",
                "efi/microsoft/boot/cdboot.efi",
                "bootmgr", "bootmgr.efi",
                "efi/boot/bootx64.efi",
            ],
            Order(plan));
    }

    [Fact]
    public void Order_IgnoresCaseOfTheBootPaths()
    {
        var plan = WindowsCopyPlan.Create(
            [File("EFI/BOOT/BOOTX64.EFI"), File("BOOTMGR"), File("Boot/BCD"), File("Sources/Install.WIM"), File("Setup.exe")],
            [],
            new WindowsCopyOptions());

        Assert.Equal(["Setup.exe", "Sources/Install.WIM", "Boot/BCD", "BOOTMGR", "EFI/BOOT/BOOTX64.EFI"], Order(plan));
    }

    [Fact]
    public void ABootmgrInASubfolder_IsNotTheBootManager()
    {
        var plan = WindowsCopyPlan.Create([File("tools/bootmgr"), File("a.txt")], [], new WindowsCopyOptions());

        Assert.Equal(["a.txt", "tools/bootmgr"], Order(plan));
    }

    [Fact]
    public void FilesUnderTheLimit_AreCopiedAsTheyAre()
    {
        var plan = WindowsCopyPlan.Create(
            TypicalSetupMedium(Gib),
            [],
            new WindowsCopyOptions { MaxFileBytes = WindowsCopyOptions.Fat32MaxFileBytes, SplitInstallImage = true });

        Assert.All(plan.Items, item => Assert.Equal(CopyAction.Copy, item.Action));
        Assert.False(plan.HasSplit);
        Assert.Equal(plan.Items.Sum(item => item.Source.Length), plan.TotalBytes);
    }

    [Fact]
    public void AnInstallWimOverFourGiB_IsSplitIntoTheSameFolder()
    {
        var plan = WindowsCopyPlan.Create(
            TypicalSetupMedium(6 * Gib),
            [],
            new WindowsCopyOptions { MaxFileBytes = WindowsCopyOptions.Fat32MaxFileBytes, SplitInstallImage = true });

        var split = Assert.Single(plan.Items, item => item.Action == CopyAction.SplitInstallImage);
        Assert.Equal("sources/install.wim", split.Source.Path);
        Assert.Equal("sources/install.swm", split.Destination);
        Assert.Equal(6 * Gib, split.Bytes);
        Assert.True(plan.HasSplit);
    }

    [Fact]
    public void AnEsdOverTheLimit_IsSplitToo()
    {
        var plan = WindowsCopyPlan.Create(
            [File("sources/install.esd", 5 * Gib)],
            [],
            new WindowsCopyOptions { MaxFileBytes = WindowsCopyOptions.Fat32MaxFileBytes, SplitInstallImage = true });

        Assert.Equal("sources/install.swm", Assert.Single(plan.Items).Destination);
    }

    [Fact]
    public void AFileOfExactlyTheLimit_FitsAndOneByteMoreDoesNot()
    {
        var options = new WindowsCopyOptions { MaxFileBytes = WindowsCopyOptions.Fat32MaxFileBytes, SplitInstallImage = true };

        var fits = WindowsCopyPlan.Create([File("sources/install.wim", WindowsCopyOptions.Fat32MaxFileBytes)], [], options);
        var split = WindowsCopyPlan.Create([File("sources/install.wim", WindowsCopyOptions.Fat32MaxFileBytes + 1)], [], options);

        Assert.Equal(CopyAction.Copy, Assert.Single(fits.Items).Action);
        Assert.Equal(CopyAction.SplitInstallImage, Assert.Single(split.Items).Action);
    }

    [Fact]
    public void AnotherFileOverTheLimit_IsRefusedWithItsSize()
    {
        var ex = Assert.Throws<BootrixException>(() => WindowsCopyPlan.Create(
            [File("sources/install.wim", Gib), File("support/huge.bin", 5 * Gib)],
            [],
            new WindowsCopyOptions { MaxFileBytes = WindowsCopyOptions.Fat32MaxFileBytes, SplitInstallImage = true }));

        Assert.Equal(ErrorCode.FileTooLargeForFileSystem, ex.Code);
        Assert.Equal(["FAT32", "5 GiB"], ex.Arguments);
    }

    [Fact]
    public void AnInstallWimOverTheLimit_IsRefusedWhenSplittingIsNotAllowed()
    {
        var ex = Assert.Throws<BootrixException>(() => WindowsCopyPlan.Create(
            [File("sources/install.wim", 5 * Gib)],
            [],
            new WindowsCopyOptions { MaxFileBytes = WindowsCopyOptions.Fat32MaxFileBytes }));

        Assert.Equal(ErrorCode.FileTooLargeForFileSystem, ex.Code);
    }

    [Fact]
    public void WithoutALimit_NothingIsSplit()
    {
        var plan = WindowsCopyPlan.Create(TypicalSetupMedium(9 * Gib), [], new WindowsCopyOptions { SplitInstallImage = true });

        Assert.All(plan.Items, item => Assert.Equal(CopyAction.Copy, item.Action));
    }

    [Fact]
    public void AnImageThatIsAlreadySplit_IsCopiedPartByPart()
    {
        var plan = WindowsCopyPlan.Create(
            [File("sources/install.swm", 3 * Gib), File("sources/install2.swm", 3 * Gib)],
            [],
            new WindowsCopyOptions { MaxFileBytes = WindowsCopyOptions.Fat32MaxFileBytes, SplitInstallImage = true });

        Assert.All(plan.Items, item => Assert.Equal(CopyAction.Copy, item.Action));
    }

    [Theory]
    [InlineData("System Volume Information/WPSettings.dat")]
    [InlineData("system volume information/IndexerVolumeGuid")]
    [InlineData("$RECYCLE.BIN/S-1-5-21/desc.ini")]
    [InlineData("pagefile.sys")]
    [InlineData("hiberfil.sys")]
    [InlineData("WPSettings.dat")]
    public void VolumeInternals_AreNotCopied(string path)
    {
        var plan = WindowsCopyPlan.Create([File(path), File("setup.exe")], [], new WindowsCopyOptions());

        Assert.Equal(["setup.exe"], Order(plan));
        Assert.Equal(path, Assert.Single(plan.Excluded).Path);
    }

    [Fact]
    public void ANestedFolderWithTheSameNameAsAVolumeInternal_IsKept()
    {
        var plan = WindowsCopyPlan.Create([File("support/pagefile.sys")], [], new WindowsCopyOptions());

        Assert.Single(plan.Items);
        Assert.Empty(plan.Excluded);
    }

    [Fact]
    public void Directories_ListEveryParentBeforeItsChildren_AndKeepEmptyOnes()
    {
        var plan = WindowsCopyPlan.Create(
            [File("efi/boot/bootx64.efi"), File("sources/dlmanifests/en-us/x.xml")],
            ["sources/$OEM$", "empty", "System Volume Information"],
            new WindowsCopyOptions());

        Assert.Equal(
            ["efi", "efi/boot", "empty", "sources", "sources/$OEM$", "sources/dlmanifests", "sources/dlmanifests/en-us"],
            plan.Directories);
    }

    [Fact]
    public void PartNames_FollowTheWimlibConvention()
    {
        Assert.Equal("install.swm", WindowsCopyPlan.PartName("install.swm", 1));
        Assert.Equal("install2.swm", WindowsCopyPlan.PartName("install.swm", 2));
        Assert.Equal("sources/install10.swm", WindowsCopyPlan.PartName("sources/install.swm", 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowsCopyPlan.PartName("install.swm", 0));
    }

    [Fact]
    public void TheDefaultPartSize_StaysClearOfTheFat32Limit()
    {
        Assert.InRange(WindowsCopyOptions.DefaultSplitPartBytes, 3L * Gib, WindowsCopyOptions.Fat32MaxFileBytes - 128L * 1024 * 1024);
    }
}
