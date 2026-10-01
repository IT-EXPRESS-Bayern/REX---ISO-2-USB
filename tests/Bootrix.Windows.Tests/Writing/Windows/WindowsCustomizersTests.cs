// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Jobs;
using Bootrix.Core.Model;
using Bootrix.Core.Profiles;
using Bootrix.Core.Storage;
using Bootrix.Windows.Broker;
using Bootrix.Windows.Storage;
using Bootrix.Windows.Writing;
using Bootrix.Windows.Writing.Windows;
using Bootrix.Windows.Writing.Windows.Customization;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Windows.Tests.Writing.Windows;

public sealed class WindowsCustomizersTests : IDisposable
{
    private readonly MediaFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    private sealed class NoDisks : IDiskService
    {
        public event EventHandler? DevicesChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyList<StorageDevice> Enumerate(DiskFilter filter) => [];

        public StorageDevice? Find(string devicePath) => null;
    }

    private sealed class CountingImpersonator : IClientImpersonator
    {
        public int Calls { get; private set; }

        public Task<T> RunAsClientAsync<T>(Func<Task<T>> action)
        {
            Calls++;
            return action();
        }
    }

    private WriteServices Services(IClientImpersonator? impersonator = null) =>
        new(new NoDisks(), new DiskPreparer(), new JobJournal(Path.Combine(_folder.Root, "journal")), NullLoggerFactory.Instance, impersonator);

    [Fact]
    public void CreateDefault_ListsTheCustomizersInTheOrderTheyRun()
    {
        var customizers = WindowsCustomizers.CreateDefault(Services());

        Assert.Equal(["unattend", "boot-manager-2023", "boot-wim-bypass", "drivers"], customizers.Select(c => c.Id));
    }

    [Fact]
    public void CreateDefault_QuickChecksComeBeforeTheSlowImageEdits()
    {
        var ids = WindowsCustomizers.CreateDefault(Services()).Select(c => c.Id).ToList();

        Assert.True(ids.IndexOf("unattend") < ids.IndexOf("boot-wim-bypass"));
        Assert.True(ids.IndexOf("boot-manager-2023") < ids.IndexOf("boot-wim-bypass"));
        Assert.True(ids.IndexOf("boot-manager-2023") < ids.IndexOf("drivers"));
    }

    [Fact]
    public void CreateDefault_IdsAreUnique()
    {
        var ids = WindowsCustomizers.CreateDefault(Services()).Select(c => c.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public void CreateDefault_ADefaultJobAsksForNothing()
    {
        var write = CustomizationKit.Write(_folder.Work, new WindowsSetupOptions(), CustomizationKit.Profile(build: 26100));

        Assert.All(WindowsCustomizers.CreateDefault(Services()), customizer => Assert.False(customizer.Applies(write), customizer.Id));
    }

    [Fact]
    public void CreateDefault_AJobWithEverythingSwitchedOnAsksForEveryCustomizer()
    {
        var windows = new WindowsSetupOptions
        {
            BypassTpm = true,
            LocalAccountName = "Techniker",
            DriverFolders = [@"C:\Treiber"],
            BootCertificate = BootCertificate.Windows2023,
        };
        var write = CustomizationKit.Write(_folder.Work, windows, CustomizationKit.Profile(build: 26200));

        Assert.All(WindowsCustomizers.CreateDefault(Services()), customizer => Assert.True(customizer.Applies(write), customizer.Id));
    }

    [Fact]
    public async Task CreateDefault_InTheBroker_DriverFoldersAreReadAsTheClient()
    {
        var impersonator = new CountingImpersonator();
        var drivers = Path.Combine(_folder.Root, "user", "pack");
        Directory.CreateDirectory(drivers);
        File.WriteAllText(Path.Combine(drivers, "a.inf"), "A");
        File.WriteAllText(Path.Combine(drivers, "a.sys"), "S");
        var write = CustomizationKit.Write(_folder.Work, new WindowsSetupOptions { DriverFolders = [drivers] });
        var customizer = WindowsCustomizers.CreateDefault(Services(impersonator)).Single(c => c.Id == "drivers");

        await customizer.ApplyAsync(CustomizationKit.Customization(write, _folder.Media), new ProgressLog(), CancellationToken.None);

        Assert.Equal(1 + 2, impersonator.Calls);
        Assert.True(_folder.Exists("$WinPEDriver$/01-pack/a.inf"));
    }

    [Fact]
    public async Task CreateDefault_WhereTheJobRunsAsTheUser_DriverFoldersAreReadDirectly()
    {
        var drivers = Path.Combine(_folder.Root, "user", "pack");
        Directory.CreateDirectory(drivers);
        File.WriteAllText(Path.Combine(drivers, "a.inf"), "A");
        var write = CustomizationKit.Write(_folder.Work, new WindowsSetupOptions { DriverFolders = [drivers] });
        var customizer = WindowsCustomizers.CreateDefault(Services()).Single(c => c.Id == "drivers");

        await customizer.ApplyAsync(CustomizationKit.Customization(write, _folder.Media), new ProgressLog(), CancellationToken.None);

        Assert.True(_folder.Exists("$WinPEDriver$/01-pack/a.inf"));
    }

    [Fact]
    public async Task ClientUserContext_RunsTheActionThroughTheImpersonator()
    {
        var impersonator = new CountingImpersonator();
        var context = new ClientUserContext(impersonator);

        var value = await context.RunAsync(() => Task.FromResult(42));

        Assert.Equal(42, value);
        Assert.Equal(1, impersonator.Calls);
    }

    [Fact]
    public async Task ClientUserContext_WhenTheClientCannotBeImpersonated_ThereIsNoFallback()
    {
        var context = new ClientUserContext(new RefusingImpersonator());

        await Assert.ThrowsAsync<InvalidOperationException>(() => context.RunAsync(() => Task.FromResult(1)));
    }

    private sealed class RefusingImpersonator : IClientImpersonator
    {
        public Task<T> RunAsClientAsync<T>(Func<Task<T>> action) => throw new InvalidOperationException("no client");
    }
}
