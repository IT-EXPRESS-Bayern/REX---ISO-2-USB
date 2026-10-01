// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Profiles;
using Bootrix.Core.Tests.Writing.Windows.Support;
using Bootrix.Core.Writing.Windows.Customization;

namespace Bootrix.Core.Tests.Writing.Windows;

public sealed class BootImagePatcherTests : IDisposable
{
    private readonly ScratchFolder _folder = new();
    private readonly FakeImageTools _tools = new();

    public void Dispose() => _folder.Dispose();

    private string BootWim => _folder.Full("sources/boot.wim");

    private BootImagePatcher Patcher() => new(new StagedImageEditor(_tools, _tools), _tools);

    private Task<bool> PatchAsync(WindowsSetupOptions options, ProgressLog? progress = null) =>
        Patcher().PatchAsync(BootWim, HardwareCheckBypass.Changes(options), _folder.Work, progress ?? new ProgressLog(), CancellationToken.None);

    [Fact]
    public async Task Patch_SetsTheRequestedValuesInTheSetupImage()
    {
        _folder.Write("sources/boot.wim", FakeWims.BootWim());

        var patched = await PatchAsync(new WindowsSetupOptions { BypassTpm = true, BypassSecureBoot = true });

        Assert.True(patched);
        Assert.Equal(
            ["mount:boot.wim:2", "load-hive:System", "unload-hive:System", "unmount:commit:2"],
            _tools.Calls);
        Assert.Equal(
            [@"System:Setup\LabConfig\BypassTPMCheck=1:DWord", @"System:Setup\LabConfig\BypassSecureBootCheck=1:DWord"],
            _tools.Registry);
    }

    [Fact]
    public async Task Patch_FindsTheSetupImageByName_NotByPosition()
    {
        var xml = Images.Wim.WimFixture.Xml(
            Images.Wim.WimFixture.Image(1, "Microsoft Windows Setup (x64)", 9, 26200, "WindowsPE"),
            Images.Wim.WimFixture.Image(2, "Microsoft Windows PE (x64)", 9, 26200, "WindowsPE"));
        _folder.Write("sources/boot.wim", Images.Wim.WimFixture.Build(xml));

        await PatchAsync(new WindowsSetupOptions { BypassRam = true });

        Assert.Contains("mount:boot.wim:1", _tools.Calls);
        Assert.DoesNotContain("mount:boot.wim:2", _tools.Calls);
    }

    [Fact]
    public async Task Patch_UnnamedImages_FallBackToTheSecondOne()
    {
        var xml = Images.Wim.WimFixture.Xml(
            Images.Wim.WimFixture.Image(1, "Custom A", 9, 26200, "WindowsPE"),
            Images.Wim.WimFixture.Image(2, "Custom B", 9, 26200, "WindowsPE"));
        _folder.Write("sources/boot.wim", Images.Wim.WimFixture.Build(xml));

        await PatchAsync(new WindowsSetupOptions { BypassRam = true });

        Assert.Contains("mount:boot.wim:2", _tools.Calls);
    }

    [Fact]
    public async Task Patch_ImageWithoutASetupImage_IsLeftAlone()
    {
        var original = FakeWims.BootWim(withSetup: false);
        _folder.Write("sources/boot.wim", original);

        var patched = await PatchAsync(new WindowsSetupOptions { BypassTpm = true });

        Assert.False(patched);
        Assert.Empty(_tools.Calls);
        Assert.Equal(original, File.ReadAllBytes(BootWim));
    }

    [Fact]
    public async Task Patch_NoBootImage_DoesNothing()
    {
        var patched = await PatchAsync(new WindowsSetupOptions { BypassTpm = true });

        Assert.False(patched);
        Assert.Empty(_tools.Calls);
    }

    [Fact]
    public async Task Patch_NothingToChange_DoesNotMount()
    {
        _folder.Write("sources/boot.wim", FakeWims.BootWim());

        var patched = await PatchAsync(new WindowsSetupOptions());

        Assert.False(patched);
        Assert.Empty(_tools.Calls);
    }

    [Fact]
    public async Task Patch_FailingRegistryChange_LeavesTheBootImageUntouched()
    {
        var original = FakeWims.BootWim();
        _folder.Write("sources/boot.wim", original);
        var failing = new FailingHiveTools(_tools);
        var patcher = new BootImagePatcher(new StagedImageEditor(_tools, failing), failing);

        await Assert.ThrowsAsync<IOException>(() =>
            patcher.PatchAsync(BootWim, HardwareCheckBypass.Changes(new WindowsSetupOptions { BypassTpm = true }), _folder.Work, new ProgressLog(), CancellationToken.None));

        Assert.Equal(original, File.ReadAllBytes(BootWim));
        Assert.Contains("discard:2", _tools.Calls);
    }

    [Fact]
    public async Task Patch_ReportsProgressToOne()
    {
        _folder.Write("sources/boot.wim", FakeWims.BootWim());
        var progress = new ProgressLog();

        await PatchAsync(new WindowsSetupOptions { BypassCpu = true }, progress);

        progress.AssertMonotonicToOne();
    }

    /// <summary>Opening the registry hive fails, as it does when the hive is still loaded by another run.</summary>
    private sealed class FailingHiveTools(FakeImageTools inner) : Bootrix.Core.Tiny.IImageFileSystem
    {
        public Bootrix.Core.Tiny.IOfflineHive LoadHive(string mountDirectory, Bootrix.Core.Tiny.RegistryHive hive) => throw new IOException("hive is locked");

        public long GetFreeBytes(string path) => inner.GetFreeBytes(path);

        public Task DeleteAsync(string path, bool takeOwnership, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task ReplaceWithEmptyFileAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RebuildWinSxsAsync(string winSxsPath, IReadOnlyList<string> keepPatterns, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task CopyDirectoryAsync(string source, string destination, Func<string, bool>? filter, IProgress<double>? progress, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
