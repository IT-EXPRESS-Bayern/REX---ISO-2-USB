// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Model;
using Bootrix.Core.Tests.Writing.Windows.Support;
using Bootrix.Core.Writing.Windows.Customization;

namespace Bootrix.Core.Tests.Writing.Windows;

public class Ca2023PolicyTests
{
    [Theory]
    [InlineData(BootCertificate.Windows2023, 0, Ca2023Mode.Required)]
    [InlineData(BootCertificate.Windows2023, 22631, Ca2023Mode.Required)]
    [InlineData(BootCertificate.Windows2023, 26100, Ca2023Mode.Required)]
    [InlineData(BootCertificate.Windows2023, 26200, Ca2023Mode.Required)]
    [InlineData(BootCertificate.Windows2011, 26200, Ca2023Mode.Off)]
    [InlineData(BootCertificate.Windows2011, 0, Ca2023Mode.Off)]
    [InlineData(BootCertificate.Auto, 0, Ca2023Mode.Off)]
    [InlineData(BootCertificate.Auto, 19045, Ca2023Mode.Off)]
    [InlineData(BootCertificate.Auto, 26100, Ca2023Mode.Off)]
    [InlineData(BootCertificate.Auto, 26199, Ca2023Mode.Off)]
    [InlineData(BootCertificate.Auto, 26200, Ca2023Mode.BestEffort)]
    [InlineData(BootCertificate.Auto, 26300, Ca2023Mode.BestEffort)]
    [InlineData(BootCertificate.Auto, 27000, Ca2023Mode.BestEffort)]
    public void Decide_FollowsTheJobAndTheBuild(BootCertificate certificate, int build, Ca2023Mode expected)
    {
        Assert.Equal(expected, Ca2023Policy.Decide(certificate, build));
    }

    [Fact]
    public void FirstBuildWithBootFiles_Is26200()
    {
        Assert.Equal(26200, Ca2023Policy.FirstBuildWithBootFiles);
    }
}

public sealed class BootManagerSwapPlanTests : IDisposable
{
    private readonly ScratchFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private string Extracted => Path.Combine(_folder.Root, "extracted");

    private BootManagerSwapPlan Plan() => BootManagerSwapPlan.Create(Extracted, _folder.Media);

    private static string Slash(string path) => path.Replace('\\', '/');

    [Fact]
    public void Create_ReplacesTheFilesThatTheMediumHasAndThatTheImageProvides()
    {
        Ca2023Fixtures.WriteExtracted(Extracted);
        Ca2023Fixtures.WriteMedia(_folder);

        var plan = Plan();

        Assert.True(plan.IsPossible);
        Assert.Equal(
            [
                "efi/boot/bootx64.efi",
                "efi/microsoft/boot/bootmgfw.efi",
                "bootmgr.efi",
                "efi/microsoft/boot/cdboot.efi",
            ],
            plan.Replacements.Where(r => r.IsEfiBinary).Select(r => Slash(r.TargetRelativePath)));
    }

    [Fact]
    public void Create_FontsAreReplacedUnderTheirOldNames_AndOnlyIfTheMediumHasThem()
    {
        Ca2023Fixtures.WriteExtracted(Extracted);
        Ca2023Fixtures.WriteMedia(_folder);

        var plan = Plan();

        var fonts = plan.Replacements.Where(r => !r.IsEfiBinary).ToList();
        Assert.Equal(["efi/microsoft/boot/fonts/segoe_slboot.ttf", "efi/microsoft/boot/fonts/wgl4_boot.ttf"], fonts.Select(f => Slash(f.TargetRelativePath)).Order());
        Assert.All(fonts, f => Assert.Contains("Fonts_EX", f.SourcePath, StringComparison.Ordinal));
    }

    [Fact]
    public void Create_FallbackLoaderComesFirstBecauseItIsWhatFirmwareStarts()
    {
        Ca2023Fixtures.WriteExtracted(Extracted);
        Ca2023Fixtures.WriteMedia(_folder);

        var first = Plan().Replacements[0];

        Assert.Equal("efi/boot/bootx64.efi", Slash(first.TargetRelativePath));
        Assert.EndsWith("bootmgfw_EX.efi", first.SourcePath, StringComparison.Ordinal);
        Assert.True(first.IsEfiBinary);
    }

    [Fact]
    public void Create_KeepsTheCasingTheMediumUses()
    {
        Ca2023Fixtures.WriteExtracted(Extracted);
        Ca2023Fixtures.WriteMedia(_folder, upper: true);

        var plan = Plan();

        Assert.Contains(plan.Replacements, r => Slash(r.TargetRelativePath) == "EFI/BOOT/BOOTX64.EFI");
        Assert.Contains(plan.Replacements, r => Slash(r.TargetRelativePath) == "BOOTMGR.EFI");
        Assert.Contains(plan.Replacements, r => Slash(r.TargetRelativePath) == "EFI/MICROSOFT/BOOT/FONTS/SEGOE_SLBOOT.TTF");
        Assert.All(plan.Replacements, r => Assert.True(File.Exists(Path.Combine(_folder.Media, r.TargetRelativePath))));
    }

    [Fact]
    public void Create_ExtractedFoldersInAnyCase_AreFound()
    {
        var lower = Path.Combine(_folder.Root, "extracted");
        Directory.CreateDirectory(Path.Combine(lower, "efi_ex"));
        File.WriteAllBytes(Path.Combine(lower, "efi_ex", "BOOTMGFW_EX.EFI"), Ca2023Fixtures.Efi(Ca2023Fixtures.New2023));
        Ca2023Fixtures.WriteMedia(_folder);

        Assert.True(Plan().IsPossible);
    }

    [Fact]
    public void Create_DoesNotAddFilesTheMediumLacks()
    {
        Ca2023Fixtures.WriteExtracted(Extracted);
        Ca2023Fixtures.WriteMedia(_folder, withBootManagerCopy: false);
        File.Delete(_folder.Full("efi/microsoft/boot/cdboot.efi"));
        File.Delete(_folder.Full("bootmgr.efi"));

        var plan = Plan();

        Assert.DoesNotContain(plan.Replacements, r => Slash(r.TargetRelativePath).EndsWith("bootmgfw.efi", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Replacements, r => Slash(r.TargetRelativePath).EndsWith("cdboot.efi", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Replacements, r => Slash(r.TargetRelativePath) == "bootmgr.efi");
        Assert.Contains(plan.Replacements, r => Slash(r.TargetRelativePath) == "efi/boot/bootx64.efi");
    }

    [Theory]
    [InlineData((ushort)0x8664, "bootx64.efi")]
    [InlineData((ushort)0xAA64, "bootaa64.efi")]
    [InlineData((ushort)0x014C, "bootia32.efi")]
    public void Create_TheLoaderNameFollowsTheArchitectureOfTheBootManager(ushort machine, string loader)
    {
        Ca2023Fixtures.WriteExtracted(Extracted, machine: machine);
        _folder.Write("efi/boot/" + loader, Ca2023Fixtures.Efi(Ca2023Fixtures.Old2011, machine));

        var plan = Plan();

        Assert.True(plan.IsPossible);
        Assert.Equal("efi/boot/" + loader, Slash(plan.Replacements[0].TargetRelativePath));
    }

    [Fact]
    public void Create_OnlyTheMatchingLoaderIsReplaced()
    {
        Ca2023Fixtures.WriteExtracted(Extracted);
        Ca2023Fixtures.WriteMedia(_folder);
        _folder.Write("efi/boot/bootaa64.efi", Ca2023Fixtures.Efi(Ca2023Fixtures.Old2011, 0xAA64));

        var plan = Plan();

        Assert.DoesNotContain(plan.Replacements, r => Slash(r.TargetRelativePath).EndsWith("bootaa64.efi", StringComparison.Ordinal));
    }

    [Fact]
    public void Create_MediumWithoutTheLoaderForThatArchitecture_IsNotPossible()
    {
        Ca2023Fixtures.WriteExtracted(Extracted, machine: 0xAA64);
        Ca2023Fixtures.WriteMedia(_folder);

        var plan = Plan();

        Assert.False(plan.IsPossible);
        Assert.Equal("Boot2023.Reason.NoLoader", plan.UnavailableReason);
        Assert.Equal("BOOTAA64.EFI", plan.UnavailableDetail);
        Assert.Empty(plan.Replacements);
    }

    [Fact]
    public void Create_ImageWithoutTheExFolder_IsNotPossible()
    {
        Directory.CreateDirectory(Extracted);
        Ca2023Fixtures.WriteMedia(_folder);

        var plan = Plan();

        Assert.False(plan.IsPossible);
        Assert.Equal("Boot2023.Reason.NoSourceFiles", plan.UnavailableReason);
    }

    [Fact]
    public void Create_ExFolderWithoutTheBootManager_IsNotPossible()
    {
        Ca2023Fixtures.WriteExtracted(Extracted, bootManager: false);
        Ca2023Fixtures.WriteMedia(_folder);

        Assert.Equal("Boot2023.Reason.NoSourceFiles", Plan().UnavailableReason);
    }

    [Fact]
    public void Create_BootManagerThatIsNotAnEfiFile_IsNotPossible()
    {
        Directory.CreateDirectory(Path.Combine(Extracted, "EFI_EX"));
        File.WriteAllText(Path.Combine(Extracted, "EFI_EX", "bootmgfw_EX.efi"), "not a PE image");
        Ca2023Fixtures.WriteMedia(_folder);

        var plan = Plan();

        Assert.False(plan.IsPossible);
        Assert.Equal("Boot2023.Reason.Unreadable", plan.UnavailableReason);
        Assert.Equal("bootmgfw_EX.efi", plan.UnavailableDetail);
    }

    [Fact]
    public void Create_FilesWithoutTheExSuffix_AreNotTreatedAsReplacements()
    {
        Ca2023Fixtures.WriteExtracted(Extracted);
        File.WriteAllBytes(Path.Combine(Extracted, "EFI_EX", "shell.efi"), Ca2023Fixtures.Efi(Ca2023Fixtures.New2023));
        File.WriteAllText(Path.Combine(Extracted, "EFI_EX", "readme.txt"), "x");
        Ca2023Fixtures.WriteMedia(_folder);
        _folder.Write("efi/microsoft/boot/shell.efi", "shell");

        var plan = Plan();

        Assert.DoesNotContain(plan.Replacements, r => Slash(r.TargetRelativePath).EndsWith("shell.efi", StringComparison.Ordinal));
    }

    [Fact]
    public void Create_ExtractedFilesNeverLeadOutsideTheMedium()
    {
        Ca2023Fixtures.WriteExtracted(Extracted);
        Ca2023Fixtures.WriteMedia(_folder);

        var plan = Plan();

        Assert.All(plan.Replacements, r =>
        {
            Assert.False(Path.IsPathRooted(r.TargetRelativePath));
            Assert.DoesNotContain("..", r.TargetRelativePath, StringComparison.Ordinal);
        });
    }
}
