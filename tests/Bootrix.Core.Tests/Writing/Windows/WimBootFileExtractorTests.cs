// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Tests.Wim;
using Bootrix.Core.Tests.Writing.Windows.Support;
using Bootrix.Core.Writing.Windows.Customization;

namespace Bootrix.Core.Tests.Writing.Windows;

/// <summary>Runs against the real wimlib: a boot.wim is built with wimcapture, as Microsoft's has a PE image and a Setup image.</summary>
public sealed class WimBootFileExtractorTests : IDisposable
{
    private readonly ScratchFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private string BootWim => _folder.Full("sources/boot.wim");

    /// <summary>Image 1 is Windows PE without the 2023 files, image 2 the Setup image with them.</summary>
    private void BuildBootWim(bool setupFirst = false)
    {
        var pe = Path.Combine(_folder.Root, "tree-pe");
        Directory.CreateDirectory(Path.Combine(pe, "Windows", "Boot", "EFI"));
        File.WriteAllText(Path.Combine(pe, "Windows", "Boot", "EFI", "bootmgfw.efi"), "old");

        var setup = Path.Combine(_folder.Root, "tree-setup");
        Ca2023Fixtures.WriteExtracted(Path.Combine(setup, "Windows", "Boot"));
        File.WriteAllText(Path.Combine(setup, "Windows", "Boot", "Fonts_EX", "ünïcode_EX.ttf"), "unicode font");
        File.WriteAllText(Path.Combine(setup, "Windows", "Boot", "EFI_EX", "bootmgfw_EX.efi.txt"), "stray");

        Directory.CreateDirectory(Path.GetDirectoryName(BootWim)!);
        var first = setupFirst ? setup : pe;
        var second = setupFirst ? pe : setup;
        var firstName = setupFirst ? "Microsoft Windows Setup (x64)" : "Microsoft Windows PE (x64)";
        var secondName = setupFirst ? "Microsoft Windows PE (x64)" : "Microsoft Windows Setup (x64)";
        Assert.Equal(0, WimTestTools.Run("wimcapture", first, BootWim, firstName, "--compress=LZX").ExitCode);
        Assert.Equal(0, WimTestTools.Run("wimappend", second, BootWim, secondName, "--compress=LZX").ExitCode);
    }

    private string Destination => Path.Combine(_folder.Root, "out");

    private static readonly string[] BootPaths = ["Windows/Boot/EFI_EX", "Windows/Boot/Fonts_EX"];

    [NeedsWimlibFact]
    public async Task Extract_PutsTheFoldersDirectlyBelowTheDestination()
    {
        BuildBootWim();

        await new WimBootFileExtractor().ExtractAsync(BootWim, 2, BootPaths, Destination, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(Destination, "EFI_EX", "bootmgfw_EX.efi")));
        Assert.True(File.Exists(Path.Combine(Destination, "EFI_EX", "bootmgr_EX.efi")));
        Assert.Equal("new segoe", File.ReadAllText(Path.Combine(Destination, "Fonts_EX", "segoe_slboot_EX.ttf")));
        Assert.Equal("unicode font", File.ReadAllText(Path.Combine(Destination, "Fonts_EX", "ünïcode_EX.ttf")));
        Assert.False(Directory.Exists(Path.Combine(Destination, "Windows")));
    }

    [NeedsWimlibFact]
    public async Task Extract_ContentIsByteForByteWhatWasCaptured()
    {
        BuildBootWim();
        var expected = File.ReadAllBytes(Path.Combine(_folder.Root, "tree-setup", "Windows", "Boot", "EFI_EX", "bootmgfw_EX.efi"));

        await new WimBootFileExtractor().ExtractAsync(BootWim, 2, BootPaths, Destination, CancellationToken.None);

        Assert.Equal(expected, File.ReadAllBytes(Path.Combine(Destination, "EFI_EX", "bootmgfw_EX.efi")));
    }

    [NeedsWimlibFact]
    public async Task Extract_ImageWithoutThePaths_ExtractsNothingAndDoesNotFail()
    {
        BuildBootWim();

        await new WimBootFileExtractor().ExtractAsync(BootWim, 1, BootPaths, Destination, CancellationToken.None);

        Assert.Empty(Directory.EnumerateFileSystemEntries(Destination));
    }

    [NeedsWimlibFact]
    public async Task Extract_OnePathMissing_StillExtractsTheOther()
    {
        BuildBootWim();

        await new WimBootFileExtractor().ExtractAsync(BootWim, 2, ["Windows/Boot/EFI_EX", "Windows/Boot/NoSuchFolder"], Destination, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(Destination, "EFI_EX", "bootmgfw_EX.efi")));
        Assert.False(Directory.Exists(Path.Combine(Destination, "NoSuchFolder")));
    }

    [NeedsWimlibFact]
    public async Task Extract_ImageThatDoesNotExist_IsAnError()
    {
        BuildBootWim();

        var ex = await Assert.ThrowsAsync<BootrixException>(() =>
            new WimBootFileExtractor().ExtractAsync(BootWim, 7, BootPaths, Destination, CancellationToken.None));

        Assert.Equal(ErrorCode.ExternalToolFailed, ex.Code);
    }

    [NeedsWimlibFact]
    public async Task Extract_FileThatIsNotAWim_IsAnError()
    {
        _folder.Write("sources/boot.wim", "not a wim");

        await Assert.ThrowsAsync<BootrixException>(() =>
            new WimBootFileExtractor().ExtractAsync(BootWim, 2, BootPaths, Destination, CancellationToken.None));
    }

    [NeedsWimlibFact]
    public async Task Extract_Cancelled_DoesNothing()
    {
        BuildBootWim();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new WimBootFileExtractor().ExtractAsync(BootWim, 2, BootPaths, Destination, cts.Token));
    }

    [NeedsWimlibFact]
    public async Task Swap_WithARealBootWim_FindsTheSetupImageByNameAndSwapsTheFiles()
    {
        BuildBootWim(setupFirst: true);
        Ca2023Fixtures.WriteMedia(_folder, withBootWim: false);
        var analyzer = new Ca2023Fixtures.MarkerAnalyzer();
        var swap = new Ca2023BootManagerSwap(new WimBootFileExtractor(), analyzer.Analyze);

        var result = await swap.ApplyAsync(_folder.Media, _folder.Work, Ca2023Mode.Required, 26200, new ProgressLog(), CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Equal("new segoe", _folder.Read("efi/microsoft/boot/fonts/segoe_slboot.ttf"));
        Assert.Contains(Ca2023Fixtures.New2023, System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(_folder.Full("efi/boot/bootx64.efi"))), StringComparison.Ordinal);
    }

    [NeedsWimlibFact]
    public async Task Swap_WithARealBootWimOfAnOlderBuild_RequiredFailsAndBestEffortSkips()
    {
        // Image 2 of this boot.wim has no EFI_EX folder, as in every image before 25H2.
        var tree = Path.Combine(_folder.Root, "tree-old");
        Directory.CreateDirectory(Path.Combine(tree, "Windows", "Boot", "EFI"));
        File.WriteAllText(Path.Combine(tree, "Windows", "Boot", "EFI", "bootmgfw.efi"), "old");
        Directory.CreateDirectory(Path.GetDirectoryName(BootWim)!);
        Assert.Equal(0, WimTestTools.Run("wimcapture", tree, BootWim, "Microsoft Windows PE (x64)").ExitCode);
        Assert.Equal(0, WimTestTools.Run("wimappend", tree, BootWim, "Microsoft Windows Setup (x64)").ExitCode);
        Ca2023Fixtures.WriteMedia(_folder, withBootWim: false);
        var before = Ca2023Fixtures.Snapshot(_folder.Media);
        var swap = new Ca2023BootManagerSwap(new WimBootFileExtractor(), new Ca2023Fixtures.MarkerAnalyzer().Analyze);

        var ex = await Assert.ThrowsAsync<BootrixException>(() => swap.ApplyAsync(_folder.Media, _folder.Work, Ca2023Mode.Required, 26100, new ProgressLog(), CancellationToken.None));
        var result = await swap.ApplyAsync(_folder.Media, _folder.Work, Ca2023Mode.BestEffort, 26100, new ProgressLog(), CancellationToken.None);

        Assert.Equal(ErrorCode.BootManager2023Unavailable, ex.Code);
        Assert.False(result.Applied);
        Assert.Equal(before, Ca2023Fixtures.Snapshot(_folder.Media));
    }

    [NeedsWimlibFact]
    public async Task Swap_BestEffort_WithACorruptBootWim_SkipsInsteadOfFailing()
    {
        Ca2023Fixtures.WriteMedia(_folder);
        _folder.Write("sources/boot.wim", new byte[300]);
        var before = Ca2023Fixtures.Snapshot(_folder.Media);
        var swap = new Ca2023BootManagerSwap(new WimBootFileExtractor(), new Ca2023Fixtures.MarkerAnalyzer().Analyze);

        var result = await swap.ApplyAsync(_folder.Media, _folder.Work, Ca2023Mode.BestEffort, 26200, new ProgressLog(), CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(before, Ca2023Fixtures.Snapshot(_folder.Media));
    }
}
