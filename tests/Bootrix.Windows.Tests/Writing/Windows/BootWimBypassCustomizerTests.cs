// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images;
using Bootrix.Core.Profiles;
using Bootrix.Core.Writing.Windows.Customization;
using Bootrix.Windows.Writing.Windows.Customization;

namespace Bootrix.Windows.Tests.Writing.Windows;

public sealed class BootWimBypassCustomizerTests : IDisposable
{
    private readonly MediaFolder _folder = new();
    private readonly FakeImageTools _tools = new();
    private readonly CapturingLogger<BootWimBypassCustomizer> _log = new();

    public void Dispose() => _folder.Dispose();

    private BootWimBypassCustomizer Customizer() =>
        new(new BootImagePatcher(new StagedImageEditor(_tools, _tools), _tools), _log);

    public static TheoryData<string, ImageKind, int, WindowsSetupOptions, bool> Requests => new()
    {
        { "tpm on 25H2", ImageKind.WindowsSetup, 26200, new WindowsSetupOptions { BypassTpm = true }, true },
        { "secure boot", ImageKind.WindowsSetup, 26100, new WindowsSetupOptions { BypassSecureBoot = true }, true },
        { "ram", ImageKind.WindowsSetup, 22631, new WindowsSetupOptions { BypassRam = true }, true },
        { "cpu", ImageKind.WindowsSetup, 26200, new WindowsSetupOptions { BypassCpu = true }, true },
        { "storage", ImageKind.WindowsSetup, 26200, new WindowsSetupOptions { BypassStorage = true }, true },
        { "build unknown", ImageKind.WindowsSetup, 0, new WindowsSetupOptions { BypassTpm = true }, true },
        { "first Windows 11 build", ImageKind.WindowsSetup, 22000, new WindowsSetupOptions { BypassTpm = true }, true },
        { "Windows 10", ImageKind.WindowsSetup, 19045, new WindowsSetupOptions { BypassTpm = true }, false },
        { "no bypass", ImageKind.WindowsSetup, 26200, new WindowsSetupOptions { LocalAccountName = "Techniker" }, false },
        { "pe", ImageKind.WindowsPe, 26200, new WindowsSetupOptions { BypassTpm = true }, false },
        { "linux", ImageKind.LinuxHybrid, 0, new WindowsSetupOptions { BypassTpm = true }, false },
    };

    [Theory]
    [MemberData(nameof(Requests))]
    public void Applies_OnlyForWindows11SetupMediaThatAsksForABypass(string name, ImageKind kind, int build, WindowsSetupOptions options, bool expected)
    {
        _ = name;
        var write = CustomizationKit.Write(_folder.Work, options, CustomizationKit.Profile(kind, build));

        Assert.Equal(expected, Customizer().Applies(write));
    }

    [Fact]
    public async Task Apply_SetsTheLabConfigValuesInTheSetupImage()
    {
        var original = FakeWim.BootWim();
        _folder.Write("sources/boot.wim", original);
        var write = CustomizationKit.Write(_folder.Work, new WindowsSetupOptions { BypassTpm = true, BypassRam = true, BypassStorage = true });
        var progress = new ProgressLog();

        await Customizer().ApplyAsync(CustomizationKit.Customization(write, _folder.Media), progress, CancellationToken.None);

        Assert.Equal(["mount:boot.wim:2", "unmount:commit:2"], _tools.Calls);
        Assert.Equal(
            [@"System:Setup\LabConfig\BypassTPMCheck=1", @"System:Setup\LabConfig\BypassRAMCheck=1", @"System:Setup\LabConfig\BypassStorageCheck=1"],
            _tools.Registry);
        Assert.EndsWith("+committed2", System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(_folder.Full("sources/boot.wim"))), StringComparison.Ordinal);
        progress.AssertMonotonicToOne();
    }

    [Fact]
    public async Task Apply_WithoutABootImage_DoesNotFailAndSaysSo()
    {
        var write = CustomizationKit.Write(_folder.Work, new WindowsSetupOptions { BypassTpm = true });
        var progress = new ProgressLog();

        await Customizer().ApplyAsync(CustomizationKit.Customization(write, _folder.Media), progress, CancellationToken.None);

        Assert.Empty(_tools.Calls);
        Assert.Contains("answer file only", _log.All, StringComparison.Ordinal);
        progress.AssertMonotonicToOne();
    }

    [Fact]
    public async Task Apply_Cancelled_LeavesTheBootImageAsItWas()
    {
        var original = FakeWim.BootWim();
        _folder.Write("sources/boot.wim", original);
        var write = CustomizationKit.Write(_folder.Work, new WindowsSetupOptions { BypassTpm = true });
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Customizer().ApplyAsync(CustomizationKit.Customization(write, _folder.Media), new ProgressLog(), cts.Token));

        Assert.Equal(original, File.ReadAllBytes(_folder.Full("sources/boot.wim")));
    }
}
