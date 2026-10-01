// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Images;
using Bootrix.Core.Profiles;
using Bootrix.Core.Writing.Windows.Customization;
using Bootrix.Windows.Writing.Windows.Customization;

namespace Bootrix.Windows.Tests.Writing.Windows;

public sealed class DriverInjectionCustomizerTests : IDisposable
{
    private readonly MediaFolder _folder = new();
    private readonly FakeImageTools _tools = new();
    private readonly FakeDriverServicing _drivers = new();
    private readonly CapturingLogger<DriverInjectionCustomizer> _log = new();

    public void Dispose() => _folder.Dispose();

    private sealed class FakeDriverServicing : IDriverServicing
    {
        public List<(string Mount, int Count)> Calls { get; } = [];

        public Task<DriverInjectionResult> AddDriversAsync(string mountDirectory, IReadOnlyList<string> infFiles, bool forceUnsigned, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            Calls.Add((mountDirectory, infFiles.Count));
            return Task.FromResult(new DriverInjectionResult(infFiles.Count, []));
        }
    }

    private sealed class CountingUserContext : IUserContext
    {
        public int Calls { get; private set; }

        public Task<T> RunAsync<T>(Func<Task<T>> action)
        {
            Calls++;
            return action();
        }
    }

    private DriverInjectionCustomizer Customizer(IUserContext? user = null) =>
        new(user ?? new ProcessUserContext(), new StagedImageEditor(_tools, _tools), _drivers, _log);

    private string DriverFolder(params (string Relative, string Content)[] files)
    {
        var root = Path.Combine(_folder.Root, "user-drivers", "Intel RST");
        foreach (var (relative, content) in files)
        {
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        return root;
    }

    private string DefaultDrivers() => DriverFolder(("iaStorVD.inf", "[Version]"), ("iaStorVD.sys", "SYS"), ("iaStorVD.cat", "CAT"));

    public static TheoryData<string, ImageKind, bool> Requests => new()
    {
        { "setup with a folder", ImageKind.WindowsSetup, true },
        { "setup without folders", ImageKind.WindowsSetup, false },
        { "pe with a folder", ImageKind.WindowsPe, false },
        { "linux with a folder", ImageKind.LinuxHybrid, false },
    };

    [Theory]
    [MemberData(nameof(Requests))]
    public void Applies_OnlyForSetupMediaWithDriverFolders(string name, ImageKind kind, bool withFolder)
    {
        _ = name;
        var windows = new WindowsSetupOptions { DriverFolders = withFolder ? [@"C:\Treiber"] : [] };
        var write = CustomizationKit.Write(_folder.Work, windows, CustomizationKit.Profile(kind));

        Assert.Equal(withFolder && kind == ImageKind.WindowsSetup, Customizer().Applies(write));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Applies_BlankFolderEntries_DoNotCount(string folder)
    {
        var write = CustomizationKit.Write(_folder.Work, new WindowsSetupOptions { DriverFolders = [folder] });

        Assert.False(Customizer().Applies(write));
    }

    [Fact]
    public async Task Apply_CopiesTheDriversToWinPEDriver_AndNothingElse()
    {
        var write = CustomizationKit.Write(_folder.Work, new WindowsSetupOptions { DriverFolders = [DefaultDrivers()] });
        var progress = new ProgressLog();

        await Customizer().ApplyAsync(CustomizationKit.Customization(write, _folder.Media), progress, CancellationToken.None);

        Assert.Equal("[Version]", _folder.Read("$WinPEDriver$/01-Intel_RST/iaStorVD.inf"));
        Assert.Equal("SYS", _folder.Read("$WinPEDriver$/01-Intel_RST/iaStorVD.sys"));
        Assert.Empty(_tools.Calls);
        Assert.Empty(_drivers.Calls);
        progress.AssertMonotonicToOne();
    }

    [Fact]
    public async Task Apply_OfflineInjectionOn_AddsTheDriversToBootAndInstallImage()
    {
        _folder.Write("sources/boot.wim", FakeWim.BootWim());
        _folder.Write("sources/install.wim", FakeWim.InstallWim(("Windows 11 Home", "Core"), ("Windows 11 Pro", "Professional")));
        var write = CustomizationKit.Write(_folder.Work, new WindowsSetupOptions { DriverFolders = [DefaultDrivers()], InjectDriversIntoImages = true });
        var progress = new ProgressLog();

        await Customizer().ApplyAsync(CustomizationKit.Customization(write, _folder.Media), progress, CancellationToken.None);

        Assert.Equal(
            ["mount:boot.wim:2", "unmount:commit:2", "mount:install.wim:1", "unmount:commit:1", "mount:install.wim:2", "unmount:commit:2"],
            _tools.Calls);
        Assert.All(_drivers.Calls, call => Assert.Equal(1, call.Count));
        Assert.Equal(3, _drivers.Calls.Count);
        progress.AssertMonotonicToOne();
    }

    [Fact]
    public async Task Apply_OfflineInjectionWithAnEdition_OnlyTouchesThatEdition()
    {
        _folder.Write("sources/boot.wim", FakeWim.BootWim());
        _folder.Write("sources/install.wim", FakeWim.InstallWim(("Windows 11 Home", "Core"), ("Windows 11 Pro", "Professional")));
        var write = CustomizationKit.Write(_folder.Work, new WindowsSetupOptions { DriverFolders = [DefaultDrivers()], InjectDriversIntoImages = true, Edition = "Windows 11 Pro" });

        await Customizer().ApplyAsync(CustomizationKit.Customization(write, _folder.Media), new ProgressLog(), CancellationToken.None);

        Assert.Contains("mount:install.wim:2", _tools.Calls);
        Assert.DoesNotContain("mount:install.wim:1", _tools.Calls);
    }

    [Fact]
    public async Task Apply_OfflineInjectionOnASplitImage_StillStagesTheFolderAndSaysWhatWasSkipped()
    {
        _folder.Write("sources/boot.wim", FakeWim.BootWim());
        _folder.Write("sources/install.swm", "part");
        var write = CustomizationKit.Write(_folder.Work, new WindowsSetupOptions { DriverFolders = [DefaultDrivers()], InjectDriversIntoImages = true });

        await Customizer().ApplyAsync(CustomizationKit.Customization(write, _folder.Media), new ProgressLog(), CancellationToken.None);

        Assert.True(_folder.Exists("$WinPEDriver$/01-Intel_RST/iaStorVD.inf"));
        Assert.Equal(["mount:boot.wim:2", "unmount:commit:2"], _tools.Calls);
        Assert.Contains("split", _log.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_ReadsTheUsersFolderThroughTheUserContext()
    {
        var user = new CountingUserContext();
        var write = CustomizationKit.Write(_folder.Work, new WindowsSetupOptions { DriverFolders = [DefaultDrivers()] });

        await Customizer(user).ApplyAsync(CustomizationKit.Customization(write, _folder.Media), new ProgressLog(), CancellationToken.None);

        Assert.Equal(1 + 3, user.Calls);
    }

    [Fact]
    public async Task Apply_FolderWithProgramsOnly_IsRefusedBeforeAnythingIsCopied()
    {
        var folder = DriverFolder(("setup.exe", "MZ"));
        var write = CustomizationKit.Write(_folder.Work, new WindowsSetupOptions { DriverFolders = [folder] });

        var ex = await Assert.ThrowsAsync<BootrixException>(() =>
            Customizer().ApplyAsync(CustomizationKit.Customization(write, _folder.Media), new ProgressLog(), CancellationToken.None));

        Assert.Equal(ErrorCode.DriverFolderRejected, ex.Code);
        Assert.False(_folder.Exists("$WinPEDriver$"));
    }

    [Fact]
    public async Task Apply_MissingFolder_IsRefused()
    {
        var write = CustomizationKit.Write(_folder.Work, new WindowsSetupOptions { DriverFolders = [Path.Combine(_folder.Root, "gone")] });

        var ex = await Assert.ThrowsAsync<BootrixException>(() =>
            Customizer().ApplyAsync(CustomizationKit.Customization(write, _folder.Media), new ProgressLog(), CancellationToken.None));

        Assert.Equal(ErrorCode.DriverFolderRejected, ex.Code);
    }

    [Fact]
    public async Task Apply_Cancelled_LeavesNothingBehind()
    {
        var write = CustomizationKit.Write(_folder.Work, new WindowsSetupOptions { DriverFolders = [DefaultDrivers()] });
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Customizer().ApplyAsync(CustomizationKit.Customization(write, _folder.Media), new ProgressLog(), cts.Token));

        Assert.False(_folder.Exists("$WinPEDriver$"));
    }
}
