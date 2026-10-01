// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Tests.Writing.Windows.Support;
using Bootrix.Core.Writing.Windows.Customization;

namespace Bootrix.Core.Tests.Writing.Windows;

public sealed class OfflineDriverInjectorTests : IDisposable
{
    private readonly ScratchFolder _folder = new();
    private readonly FakeImageTools _tools = new();
    private readonly FakeDriverServicing _drivers = new();
    private readonly CapturingLogger _log = new();

    public void Dispose() => _folder.Dispose();

    private sealed class FakeDriverServicing : IDriverServicing
    {
        public List<(string Mount, IReadOnlyList<string> Infs, bool ForceUnsigned)> Calls { get; } = [];

        /// <summary>INF file names that the image refuses.</summary>
        public HashSet<string> Refused { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<DriverInjectionResult> AddDriversAsync(string mountDirectory, IReadOnlyList<string> infFiles, bool forceUnsigned, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            Calls.Add((mountDirectory, infFiles, forceUnsigned));
            var failed = infFiles.Where(inf => Refused.Contains(Path.GetFileName(inf))).Select(inf => new DriverFailure(inf, "0x800F0203 not signed")).ToList();
            return Task.FromResult(new DriverInjectionResult(infFiles.Count - failed.Count, failed));
        }
    }

    private OfflineDriverInjector Injector() => new(new StagedImageEditor(_tools, _tools), _drivers, _log);

    private OfflineInjectionTarget BootTarget() => new(_folder.Write("sources/boot.wim", FakeWims.BootWim()), [2], "boot.wim");

    private Task<int> InjectAsync(OfflineInjectionTarget target, IReadOnlyList<string> infs, ProgressLog? progress = null) =>
        Injector().InjectAsync(target, infs, _folder.Work, progress ?? new ProgressLog(), CancellationToken.None);

    [Fact]
    public async Task Inject_AddsTheDriversToTheMountedImage()
    {
        var added = await InjectAsync(BootTarget(), ["/media/$WinPEDriver$/01-a/a.inf", "/media/$WinPEDriver$/01-a/b.inf"]);

        Assert.Equal(2, added);
        Assert.Equal(["mount:boot.wim:2", "unmount:commit:2"], _tools.Calls);
        var call = Assert.Single(_drivers.Calls);
        Assert.Equal(2, call.Infs.Count);
        Assert.StartsWith(_folder.Work, call.Mount, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inject_NeverForcesUnsignedDrivers()
    {
        await InjectAsync(BootTarget(), ["a.inf"]);

        Assert.False(Assert.Single(_drivers.Calls).ForceUnsigned);
        Assert.False(OfflineDriverInjector.ForceUnsigned);
    }

    [Fact]
    public async Task Inject_EveryIndexOfTheImageGetsTheDrivers()
    {
        var install = new OfflineInjectionTarget(_folder.Write("sources/install.wim", FakeWims.InstallWim(("Home", "Core"), ("Pro", "Professional"))), [1, 2], "install.wim");

        var added = await InjectAsync(install, ["a.inf"]);

        Assert.Equal(2, added);
        Assert.Equal(2, _drivers.Calls.Count);
        Assert.Equal(["mount:install.wim:1", "unmount:commit:1", "mount:install.wim:2", "unmount:commit:2"], _tools.Calls);
    }

    [Fact]
    public async Task Inject_OneRefusedDriver_DoesNotStopTheOthers()
    {
        _drivers.Refused.Add("bad.inf");

        var added = await InjectAsync(BootTarget(), ["good.inf", "bad.inf", "fine.inf"]);

        Assert.Equal(2, added);
        Assert.Contains("bad.inf", _log.All, StringComparison.Ordinal);
        Assert.Contains("unmount:commit:2", _tools.Calls);
    }

    [Fact]
    public async Task Inject_NoDriverAccepted_FailsAndLeavesTheImageAlone()
    {
        var target = BootTarget();
        var before = File.ReadAllBytes(target.ImagePath);
        _drivers.Refused.Add("a.inf");

        var ex = await Assert.ThrowsAsync<BootrixException>(() => InjectAsync(target, ["a.inf"]));

        Assert.Equal(ErrorCode.DriverInjectionFailed, ex.Code);
        Assert.Equal("1", ex.Arguments[0]);
        Assert.Equal("boot.wim", ex.Arguments[1]);
        Assert.DoesNotContain("unmount:commit:2", _tools.Calls);
        Assert.Equal(before, File.ReadAllBytes(target.ImagePath));
    }

    [Fact]
    public async Task Inject_NoDrivers_DoesNotTouchTheImage()
    {
        var target = BootTarget();

        var added = await InjectAsync(target, []);

        Assert.Equal(0, added);
        Assert.Empty(_tools.Calls);
        Assert.Empty(_drivers.Calls);
    }

    [Fact]
    public async Task Inject_ReportsProgressToOne()
    {
        var progress = new ProgressLog();

        await InjectAsync(BootTarget(), ["a.inf"], progress);

        progress.AssertMonotonicToOne();
    }
}

public sealed class OfflineInjectionPlanTests : IDisposable
{
    private readonly ScratchFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private void BootAndInstall(params (string Name, string EditionId)[] editions)
    {
        _folder.Write("sources/boot.wim", FakeWims.BootWim());
        _folder.Write("sources/install.wim", FakeWims.InstallWim(editions));
    }

    [Fact]
    public void Create_BootImageAndEveryEdition_WhenNoEditionIsNamed()
    {
        BootAndInstall(("Windows 11 Home", "Core"), ("Windows 11 Pro", "Professional"), ("Windows 11 Education", "Education"));

        var plan = OfflineInjectionPlan.Create(_folder.Media, null);

        Assert.Equal(["boot.wim", "install.wim"], plan.Targets.Select(t => t.Description));
        Assert.Equal([2], plan.Targets[0].Indexes);
        Assert.Equal([1, 2, 3], plan.Targets[1].Indexes);
        Assert.Empty(plan.Skipped);
    }

    [Theory]
    [InlineData("Windows 11 Pro", 2)]
    [InlineData("windows 11 pro", 2)]
    [InlineData("Professional", 2)]
    [InlineData("Core", 1)]
    public void Create_NamedEdition_OnlyThatImageOfTheInstallImage(string edition, int index)
    {
        BootAndInstall(("Windows 11 Home", "Core"), ("Windows 11 Pro", "Professional"));

        var plan = OfflineInjectionPlan.Create(_folder.Media, edition);

        Assert.Equal([index], plan.Targets.Single(t => t.Description == "install.wim").Indexes);
    }

    [Fact]
    public void Create_EditionTheImageLacks_SkipsTheInstallImage()
    {
        BootAndInstall(("Windows 11 Home", "Core"));

        var plan = OfflineInjectionPlan.Create(_folder.Media, "Windows 11 Enterprise");

        Assert.Equal(["boot.wim"], plan.Targets.Select(t => t.Description));
        Assert.Contains("Windows 11 Enterprise", Assert.Single(plan.Skipped), StringComparison.Ordinal);
    }

    [Fact]
    public void Create_SplitInstallImage_CannotBeMounted()
    {
        _folder.Write("sources/boot.wim", FakeWims.BootWim());
        _folder.Write("sources/install.swm", "part");
        _folder.Write("sources/install2.swm", "part");

        var plan = OfflineInjectionPlan.Create(_folder.Media, null);

        Assert.Equal(["boot.wim"], plan.Targets.Select(t => t.Description));
        Assert.Contains("split", Assert.Single(plan.Skipped), StringComparison.Ordinal);
    }

    [Fact]
    public void Create_EsdInstallImage_CannotBeMounted()
    {
        _folder.Write("sources/boot.wim", FakeWims.BootWim());
        _folder.Write("sources/install.esd", "esd");

        var plan = OfflineInjectionPlan.Create(_folder.Media, null);

        Assert.Single(plan.Targets);
        Assert.Contains("ESD", Assert.Single(plan.Skipped), StringComparison.Ordinal);
    }

    [Fact]
    public void Create_BootImageWithoutSetup_IsSkipped()
    {
        _folder.Write("sources/boot.wim", FakeWims.BootWim(withSetup: false));
        _folder.Write("sources/install.wim", FakeWims.InstallWim(("Windows 11 Pro", "Professional")));

        var plan = OfflineInjectionPlan.Create(_folder.Media, null);

        Assert.Equal(["install.wim"], plan.Targets.Select(t => t.Description));
        Assert.Contains("Setup", Assert.Single(plan.Skipped), StringComparison.Ordinal);
    }

    [Fact]
    public void Create_EmptyMedium_HasNothingToDo()
    {
        var plan = OfflineInjectionPlan.Create(_folder.Media, null);

        Assert.Empty(plan.Targets);
        Assert.Equal(2, plan.Skipped.Count);
    }

    [Fact]
    public void Create_CorruptInstallImage_IsAnError()
    {
        _folder.Write("sources/install.wim", "this is not a wim");

        Assert.ThrowsAny<Exception>(() => OfflineInjectionPlan.Create(_folder.Media, null));
    }
}
