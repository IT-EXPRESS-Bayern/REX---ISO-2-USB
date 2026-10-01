// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Bootrix.Core.Boot;
using Bootrix.Core.Errors;
using Bootrix.Core.Tests.Boot;
using Bootrix.Core.Tests.Writing.Windows.Support;
using Bootrix.Core.Writing.Windows.Customization;

namespace Bootrix.Core.Tests.Writing.Windows;

public sealed class Ca2023BootManagerSwapTests(AuthorityFixture authorities) : IClassFixture<AuthorityFixture>, IDisposable
{
    private readonly ScratchFolder _folder = new();
    private readonly CapturingLogger _log = new();
    private readonly Ca2023Fixtures.MarkerAnalyzer _analyzer = new();

    public void Dispose() => _folder.Dispose();

    private string Extracted => Path.Combine(_folder.Root, "extracted");

    private Ca2023BootManagerSwap Swap(Ca2023Fixtures.FolderExtractor extractor) => new(extractor, _analyzer.Analyze, _log);

    private Ca2023Fixtures.FolderExtractor PrepareExtractor(Action<string>? shape = null, bool upper = false)
    {
        Ca2023Fixtures.WriteExtracted(Extracted);
        shape?.Invoke(Extracted);
        Ca2023Fixtures.WriteMedia(_folder, upper);
        return new Ca2023Fixtures.FolderExtractor(Extracted);
    }

    private Task<BootManagerSwapResult> ApplyAsync(Ca2023BootManagerSwap swap, Ca2023Mode mode = Ca2023Mode.Required, int build = 26200, ProgressLog? progress = null, CancellationToken cancellationToken = default) =>
        swap.ApplyAsync(_folder.Media, _folder.Work, mode, build, progress ?? new ProgressLog(), cancellationToken);

    [Fact]
    public async Task Apply_ReplacesTheBootFilesAndKeepsTheRest()
    {
        var extractor = PrepareExtractor();
        var memtest = File.ReadAllBytes(_folder.Full("efi/microsoft/boot/memtest.efi"));
        var bcd = _folder.Read("efi/microsoft/boot/bcd");

        var result = await ApplyAsync(Swap(extractor));

        Assert.True(result.Applied);
        Assert.Null(result.SkippedReason);
        Assert.Equal(EfiMediaVerdict.Only2023, result.Verdict);
        Assert.Contains(Ca2023Fixtures.New2023, Encoding.ASCII.GetString(File.ReadAllBytes(_folder.Full("efi/boot/bootx64.efi"))), StringComparison.Ordinal);
        Assert.Contains("bootmgfw", Encoding.ASCII.GetString(File.ReadAllBytes(_folder.Full("efi/boot/bootx64.efi"))), StringComparison.Ordinal);
        Assert.Contains("bootmgfw", Encoding.ASCII.GetString(File.ReadAllBytes(_folder.Full("efi/microsoft/boot/bootmgfw.efi"))), StringComparison.Ordinal);
        Assert.Contains("bootmgr", Encoding.ASCII.GetString(File.ReadAllBytes(_folder.Full("bootmgr.efi"))), StringComparison.Ordinal);
        Assert.Contains("cdboot", Encoding.ASCII.GetString(File.ReadAllBytes(_folder.Full("efi/microsoft/boot/cdboot.efi"))), StringComparison.Ordinal);
        Assert.Equal("new segoe", _folder.Read("efi/microsoft/boot/fonts/segoe_slboot.ttf"));
        Assert.Equal("new wgl4", _folder.Read("efi/microsoft/boot/fonts/wgl4_boot.ttf"));
        Assert.Equal(memtest, File.ReadAllBytes(_folder.Full("efi/microsoft/boot/memtest.efi")));
        Assert.Equal(bcd, _folder.Read("efi/microsoft/boot/bcd"));
        Assert.Equal("bootmgr", _folder.Read("bootmgr"));
        Assert.False(_folder.Exists("efi/microsoft/boot/fonts/chs_boot.ttf"));
    }

    [Fact]
    public async Task Apply_ListsWhatWasReplaced()
    {
        var extractor = PrepareExtractor();

        var result = await ApplyAsync(Swap(extractor));

        Assert.Equal(6, result.Replaced.Count);
        Assert.Contains(result.Replaced, r => r.Replace('\\', '/') == "efi/boot/bootx64.efi");
        Assert.Contains(result.Replaced, r => r.Replace('\\', '/') == "efi/microsoft/boot/fonts/segoe_slboot.ttf");
    }

    [Fact]
    public async Task Apply_TakesTheFilesFromTheSetupImageOfBootWim()
    {
        var extractor = PrepareExtractor();

        await ApplyAsync(Swap(extractor));

        var call = Assert.Single(extractor.Calls);
        Assert.Equal(_folder.Full("sources/boot.wim"), call.Image);
        Assert.Equal(["Windows/Boot/EFI_EX", "Windows/Boot/Fonts_EX"], call.Paths);
    }

    [Fact]
    public async Task Apply_AsksTheAnalyserAboutTheReplacedLoadersAndTheFallbackLoader()
    {
        var extractor = PrepareExtractor();

        await ApplyAsync(Swap(extractor));

        Assert.Equal(
            ["efi/boot/bootx64.efi", "efi/microsoft/boot/bootmgfw.efi", "bootmgr.efi", "efi/microsoft/boot/cdboot.efi"],
            _analyzer.Labels.Select(l => l.Replace('\\', '/')));
    }

    [Fact]
    public async Task Apply_ReleasesTheFilesBeforeItReturns()
    {
        var extractor = PrepareExtractor();

        await ApplyAsync(Swap(extractor));

        Assert.NotEmpty(_analyzer.Streams);
        Assert.All(_analyzer.Streams, stream => Assert.False(stream.CanRead));
    }

    [Fact]
    public async Task Apply_LeavesNoTemporaryFilesOnTheMedium()
    {
        var extractor = PrepareExtractor();

        await ApplyAsync(Swap(extractor));

        Assert.DoesNotContain(Directory.EnumerateFiles(_folder.Media, "*", SearchOption.AllDirectories), f => f.EndsWith(".bootrix-new", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Apply_ReadOnlyFilesFromAnIso_AreReplaced()
    {
        var extractor = PrepareExtractor();
        foreach (var file in Directory.EnumerateFiles(_folder.Media, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.ReadOnly);
        }

        var result = await ApplyAsync(Swap(extractor));

        Assert.True(result.Applied);
    }

    [Fact]
    public async Task Apply_MediumWithUpperCaseNames_IsSwappedInPlace()
    {
        var extractor = PrepareExtractor(upper: true);

        var result = await ApplyAsync(Swap(extractor));

        Assert.True(result.Applied);
        Assert.Contains(Ca2023Fixtures.New2023, Encoding.ASCII.GetString(File.ReadAllBytes(_folder.Full("EFI/BOOT/BOOTX64.EFI"))), StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(_folder.Full("EFI/BOOT")));
    }

    [Fact]
    public async Task Apply_ReportsProgressToOne()
    {
        var extractor = PrepareExtractor();
        var progress = new ProgressLog();

        await ApplyAsync(Swap(extractor), progress: progress);

        progress.AssertMonotonicToOne();
    }

    [Fact]
    public async Task Apply_ModeOff_DoesNothing()
    {
        var extractor = PrepareExtractor();
        var before = Ca2023Fixtures.Snapshot(_folder.Media);

        var result = await ApplyAsync(Swap(extractor), Ca2023Mode.Off);

        Assert.False(result.Applied);
        Assert.Empty(extractor.Calls);
        Assert.Equal(before, Ca2023Fixtures.Snapshot(_folder.Media));
    }

    [Fact]
    public async Task Apply_ResultNotSignedAsExpected_RequiredFailsAndPutsTheOriginalsBack()
    {
        var extractor = PrepareExtractor(root => File.WriteAllBytes(Path.Combine(root, "EFI_EX", "bootmgfw_EX.efi"), Ca2023Fixtures.Efi(Ca2023Fixtures.Old2011)));
        var before = Ca2023Fixtures.Snapshot(_folder.Media);

        var ex = await Assert.ThrowsAsync<BootrixException>(() => ApplyAsync(Swap(extractor)));

        Assert.Equal(ErrorCode.BootManager2023Unverified, ex.Code);
        var details = Assert.IsType<string>(ex.Arguments[0]);
        Assert.Contains("efi/boot/bootx64.efi", details, StringComparison.Ordinal);
        Assert.Contains("Microsoft Windows Production PCA 2011", details, StringComparison.Ordinal);
        Assert.Equal(before, Ca2023Fixtures.Snapshot(_folder.Media));
    }

    [Fact]
    public async Task Apply_ResultNotSignedAsExpected_BestEffortKeepsTheMediumAsItWas()
    {
        var extractor = PrepareExtractor(root => File.WriteAllBytes(Path.Combine(root, "EFI_EX", "bootmgfw_EX.efi"), Ca2023Fixtures.Efi(Ca2023Fixtures.Old2011)));
        var before = Ca2023Fixtures.Snapshot(_folder.Media);

        var result = await ApplyAsync(Swap(extractor), Ca2023Mode.BestEffort);

        Assert.False(result.Applied);
        Assert.Contains("PCA 2011", result.SkippedReason, StringComparison.Ordinal);
        Assert.Equal(before, Ca2023Fixtures.Snapshot(_folder.Media));
        Assert.Contains("taken back", _log.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_UnsignedReplacement_IsRefused()
    {
        var extractor = PrepareExtractor(root => File.WriteAllBytes(Path.Combine(root, "EFI_EX", "bootmgfw_EX.efi"), Ca2023Fixtures.Efi("UNSIGNED")));
        var before = Ca2023Fixtures.Snapshot(_folder.Media);

        var ex = await Assert.ThrowsAsync<BootrixException>(() => ApplyAsync(Swap(extractor)));

        Assert.Equal(ErrorCode.BootManager2023Unverified, ex.Code);
        Assert.Equal(before, Ca2023Fixtures.Snapshot(_folder.Media));
    }

    [Fact]
    public async Task Apply_OneOfSeveralLoadersWithTheWrongSignature_RollsBackAll()
    {
        var extractor = PrepareExtractor(root => File.WriteAllBytes(Path.Combine(root, "EFI_EX", "cdboot_EX.efi"), Ca2023Fixtures.Efi(Ca2023Fixtures.Old2011)));
        var before = Ca2023Fixtures.Snapshot(_folder.Media);

        var ex = await Assert.ThrowsAsync<BootrixException>(() => ApplyAsync(Swap(extractor)));

        Assert.Contains("cdboot.efi", Assert.IsType<string>(ex.Arguments[0]), StringComparison.Ordinal);
        Assert.Equal(before, Ca2023Fixtures.Snapshot(_folder.Media));
    }

    [Fact]
    public async Task Apply_ImageWithoutTheExFiles_RequiredFailsNamingTheBuild()
    {
        var extractor = PrepareExtractor(root => Directory.Delete(Path.Combine(root, "EFI_EX"), recursive: true));
        var before = Ca2023Fixtures.Snapshot(_folder.Media);

        var ex = await Assert.ThrowsAsync<BootrixException>(() => ApplyAsync(Swap(extractor), build: 26100));

        Assert.Equal(ErrorCode.BootManager2023Unavailable, ex.Code);
        Assert.Equal("26100", ex.Arguments[0]);
        Assert.Contains("bootmgfw_EX.efi", Assert.IsType<string>(ex.Arguments[1]), StringComparison.Ordinal);
        Assert.Equal(before, Ca2023Fixtures.Snapshot(_folder.Media));
    }

    [Fact]
    public async Task Apply_ImageWithoutTheExFiles_BestEffortLeavesTheMediumAlone()
    {
        var extractor = PrepareExtractor(root => Directory.Delete(Path.Combine(root, "EFI_EX"), recursive: true));
        var before = Ca2023Fixtures.Snapshot(_folder.Media);

        var result = await ApplyAsync(Swap(extractor), Ca2023Mode.BestEffort);

        Assert.False(result.Applied);
        Assert.NotNull(result.SkippedReason);
        Assert.Equal(before, Ca2023Fixtures.Snapshot(_folder.Media));
        Assert.Contains("keeps its boot files", _log.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_UnknownBuild_IsShownAsAQuestionMark()
    {
        var extractor = PrepareExtractor(root => Directory.Delete(Path.Combine(root, "EFI_EX"), recursive: true));

        var ex = await Assert.ThrowsAsync<BootrixException>(() => ApplyAsync(Swap(extractor), build: 0));

        Assert.Equal("?", ex.Arguments[0]);
    }

    [Fact]
    public async Task Apply_MediumWithoutBootWim_Required_Fails()
    {
        var extractor = PrepareExtractor();
        File.Delete(_folder.Full("sources/boot.wim"));

        var ex = await Assert.ThrowsAsync<BootrixException>(() => ApplyAsync(Swap(extractor)));

        Assert.Equal(ErrorCode.BootManager2023Unavailable, ex.Code);
        Assert.Empty(extractor.Calls);
    }

    [Fact]
    public async Task Apply_MediumWithoutTheLoaderOfThatArchitecture_Required_Fails()
    {
        var extractor = PrepareExtractor();
        File.Delete(_folder.Full("efi/boot/bootx64.efi"));

        var ex = await Assert.ThrowsAsync<BootrixException>(() => ApplyAsync(Swap(extractor)));

        Assert.Equal(ErrorCode.BootManager2023Unavailable, ex.Code);
        Assert.Contains("BOOTX64.EFI", Assert.IsType<string>(ex.Arguments[1]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_FailureInTheMiddleOfTheSwap_PutsTheFirstFilesBack()
    {
        var extractor = PrepareExtractor();
        var before = Ca2023Fixtures.Snapshot(_folder.Media);

        // The second file to be replaced cannot be written because its temporary name is taken by a folder.
        Directory.CreateDirectory(_folder.Full("efi/microsoft/boot/bootmgfw.efi.bootrix-new"));

        await Assert.ThrowsAnyAsync<Exception>(() => ApplyAsync(Swap(extractor)));

        Directory.Delete(_folder.Full("efi/microsoft/boot/bootmgfw.efi.bootrix-new"));
        Assert.Equal(before, Ca2023Fixtures.Snapshot(_folder.Media));
    }

    [Fact]
    public async Task Apply_CancelledWhileVerifying_PutsTheOriginalsBack()
    {
        var extractor = PrepareExtractor();
        var before = Ca2023Fixtures.Snapshot(_folder.Media);
        using var cts = new CancellationTokenSource();
        _analyzer.OnAnalyze = cts.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ApplyAsync(Swap(extractor), cancellationToken: cts.Token));

        Assert.Equal(before, Ca2023Fixtures.Snapshot(_folder.Media));
    }

    [Fact]
    public async Task Apply_CancelledBeforeTheStart_ChangesNothing()
    {
        var extractor = PrepareExtractor();
        var before = Ca2023Fixtures.Snapshot(_folder.Media);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ApplyAsync(Swap(extractor), cancellationToken: cts.Token));

        Assert.Equal(before, Ca2023Fixtures.Snapshot(_folder.Media));
    }

    [Fact]
    public async Task Apply_LeavesNoScratchFolderInTheWorkFolder_AlsoAfterAFailure()
    {
        var extractor = PrepareExtractor();
        await ApplyAsync(Swap(extractor));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_folder.Work));

        var failing = PrepareExtractor(root => File.WriteAllBytes(Path.Combine(root, "EFI_EX", "bootmgfw_EX.efi"), Ca2023Fixtures.Efi(Ca2023Fixtures.Old2011)));
        await Assert.ThrowsAsync<BootrixException>(() => ApplyAsync(Swap(failing)));

        Assert.Empty(Directory.EnumerateFileSystemEntries(_folder.Work));
    }

    [Fact]
    public async Task Apply_TwoMediaOfOneJobInParallel_ShareTheWorkFolderWithoutTrippingOverEachOther()
    {
        var extractor = PrepareExtractor();
        using var second = new ScratchFolder();
        Ca2023Fixtures.WriteMedia(second);
        var swap = Swap(extractor);

        var results = await Task.WhenAll(
            swap.ApplyAsync(_folder.Media, _folder.Work, Ca2023Mode.Required, 26200, new ProgressLog(), CancellationToken.None),
            swap.ApplyAsync(second.Media, _folder.Work, Ca2023Mode.Required, 26200, new ProgressLog(), CancellationToken.None));

        Assert.All(results, result => Assert.True(result.Applied));
        Assert.Contains(Ca2023Fixtures.New2023, Encoding.ASCII.GetString(File.ReadAllBytes(second.Full("efi/boot/bootx64.efi"))), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_RepeatedOnTheSameMedium_WorksAgain()
    {
        var extractor = PrepareExtractor();

        await ApplyAsync(Swap(extractor));
        var result = await ApplyAsync(Swap(extractor));

        Assert.True(result.Applied);
    }

    [Fact]
    public async Task Apply_WithTheRealAnalyser_AFileSignedByAnotherCaIsNotAccepted()
    {
        // A signature of the Windows UEFI CA 2023 can only be made by Microsoft; a file signed by a test CA must fail the check.
        var signed = SignedEfi();
        var extractor = PrepareExtractor(root => File.WriteAllBytes(Path.Combine(root, "EFI_EX", "bootmgfw_EX.efi"), signed));
        var before = Ca2023Fixtures.Snapshot(_folder.Media);
        var swap = new Ca2023BootManagerSwap(extractor, logger: _log);

        var ex = await Assert.ThrowsAsync<BootrixException>(() => ApplyAsync(swap));

        Assert.Equal(ErrorCode.BootManager2023Unverified, ex.Code);
        Assert.Contains("other", Assert.IsType<string>(ex.Arguments[0]), StringComparison.Ordinal);
        Assert.Equal(before, Ca2023Fixtures.Snapshot(_folder.Media));
    }

    [Fact]
    public async Task Apply_WithTheRealAnalyser_UnsignedFilesAreNotAccepted()
    {
        var extractor = PrepareExtractor(root => File.WriteAllBytes(Path.Combine(root, "EFI_EX", "bootmgfw_EX.efi"), Ca2023Fixtures.Efi("plain")));
        var before = Ca2023Fixtures.Snapshot(_folder.Media);
        var swap = new Ca2023BootManagerSwap(extractor, logger: _log);

        var result = await ApplyAsync(swap, Ca2023Mode.BestEffort);

        Assert.False(result.Applied);
        Assert.Equal(before, Ca2023Fixtures.Snapshot(_folder.Media));
    }

    private byte[] SignedEfi()
    {
        PeBuilder Make() => new PeBuilder().AddSection(".text", PeBuilder.Pattern(0x300, 7));
        var block = authorities.Own.Sign(Make().Build());
        return Make().AddCertificate(block).Build();
    }
}
