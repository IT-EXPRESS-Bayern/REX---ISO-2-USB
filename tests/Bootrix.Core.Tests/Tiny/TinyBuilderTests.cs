// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Images;
using Bootrix.Core.Jobs;
using Bootrix.Core.Profiles;
using Bootrix.Core.Tiny;
using Bootrix.Core.Unattend;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Core.Tests.Tiny;

public sealed class TinyBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bootrix-tiny-" + Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly string _work;
    private readonly FakeServices _fake = new();

    public TinyBuilderTests()
    {
        _source = Path.Combine(_root, "iso");
        _work = Path.Combine(_root, "work");
        Directory.CreateDirectory(Path.Combine(_source, "sources"));
        Directory.CreateDirectory(Path.Combine(_source, "efi", "boot"));
        File.WriteAllText(Path.Combine(_source, "sources", "install.wim"), "original install image");
        File.WriteAllText(Path.Combine(_source, "sources", "boot.wim"), "boot image");
        File.WriteAllText(Path.Combine(_source, "sources", "setup.exe"), "setup");
        File.WriteAllText(Path.Combine(_source, "bootmgr"), "bootmgr");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private TinyBuildOptions Options(string profile = "tiny11") => new()
    {
        SourceRoot = _source,
        WorkDirectory = _work,
        ImageIndex = 6,
        ProfileId = profile,
    };

    private TinyBuilder Builder() => new(_fake, _fake, _fake, _fake);

    private static async Task<JobResult> RunAsync(IJob job, CancellationToken cancellationToken = default) =>
        await new JobRunner(NullLogger<JobRunner>.Instance).RunAsync(job, cancellationToken: cancellationToken);

    [Fact]
    public async Task RunsTheStepsInOrderAndLeavesTheMediaFolder()
    {
        _fake.Appx = [new ProvisionedAppx("Microsoft.BingNews_1", "News")];

        var result = await RunAsync(Builder().CreateJob(Options()));

        Assert.True(result.Succeeded, result.Error?.ToString());
        var expectedOrder = new[]
        {
            "copy-media", "export:6->install.wim:Maximum", "arch", "mount:install.wim:1", "list-appx", "remove-appx",
            "delete-files", "load-hive:", "cleanup-store", "unmount:commit", "export:1->install.wim.tmp:Maximum",
            "mount:boot.wim:2", "unmount:commit",
        };

        var position = -1;
        foreach (var step in expectedOrder)
        {
            var next = _fake.Calls.FindIndex(position + 1, c => c.StartsWith(step, StringComparison.Ordinal));
            Assert.True(next > position, $"'{step}' is missing or out of order in: {string.Join(", ", _fake.Calls)}");
            position = next;
        }

        var media = Path.Combine(_work, "media");
        Assert.Equal("exported install image", File.ReadAllText(Path.Combine(media, "sources", "install.wim")));
        Assert.True(File.Exists(Path.Combine(media, "bootmgr")));
        Assert.True(File.Exists(Path.Combine(media, "sources", "setup.exe")));
        Assert.False(File.Exists(Path.Combine(media, "sources", "install.wim.tmp")));
    }

    [Fact]
    public async Task EachHiveIsLoadedOnlyOncePerImage()
    {
        await RunAsync(Builder().CreateJob(Options()));

        var loads = _fake.Calls.Where(c => c.StartsWith("load-hive:", StringComparison.Ordinal)).ToList();
        // Four hives for the install image, three for the boot image (it has no software changes).
        Assert.Equal(7, loads.Count);
        Assert.Equal(4, loads.Distinct().Count());
    }

    [Fact]
    public async Task OnlyMatchingProvisionedAppsAreRemoved()
    {
        _fake.Appx =
        [
            new ProvisionedAppx("Microsoft.BingNews_4.0_neutral_~_8wekyb3d8bbwe", "BingNews"),
            new ProvisionedAppx("Microsoft.WindowsStore_22_neutral_~_8wekyb3d8bbwe", "Store"),
            new ProvisionedAppx("MICROSOFT.XBOXAPP_5_neutral_~_8wekyb3d8bbwe", "Xbox"),
            new ProvisionedAppx("Microsoft.WindowsTerminal_1_neutral_~_8wekyb3d8bbwe", "Terminal"),
        ];

        await RunAsync(Builder().CreateJob(Options()));

        Assert.Equal(
            ["Microsoft.BingNews_4.0_neutral_~_8wekyb3d8bbwe", "MICROSOFT.XBOXAPP_5_neutral_~_8wekyb3d8bbwe"],
            _fake.RemovedAppx);
    }

    [Fact]
    public async Task DisabledGroupsAreLeftAlone()
    {
        _fake.Appx = [new ProvisionedAppx("Microsoft.Copilot_1", "Copilot"), new ProvisionedAppx("Microsoft.BingNews_1", "News")];
        var options = Options() with { DisabledGroups = new HashSet<string> { "edge", "copilot", "telemetry" } };

        await RunAsync(Builder().CreateJob(options));

        Assert.Equal(["Microsoft.BingNews_1"], _fake.RemovedAppx);
        Assert.DoesNotContain(_fake.Deleted, d => d.Contains("Edge", StringComparison.Ordinal));
        Assert.DoesNotContain(_fake.Deleted, d => d.Contains("Tasks", StringComparison.Ordinal));
        Assert.DoesNotContain(_fake.HiveActions, a => a.Contains("TurnOffWindowsCopilot", StringComparison.Ordinal));
        Assert.Contains(_fake.Deleted, d => d.Contains("OneDriveSetup.exe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EdgeAndOneDriveFilesAreDeletedWithOwnershipWhereNeeded()
    {
        await RunAsync(Builder().CreateJob(Options()));

        Assert.Contains(_fake.Deleted, d => d.Contains("Microsoft-Edge-Webview", StringComparison.Ordinal) && d.EndsWith("|owner", StringComparison.Ordinal));
        Assert.Contains(_fake.Deleted, d => d.Contains("OneDriveSetup.exe", StringComparison.Ordinal) && d.EndsWith("|owner", StringComparison.Ordinal));
        Assert.Contains(_fake.Deleted, d => d.Contains(Path.Combine("Program Files (x86)", "Microsoft", "Edge"), StringComparison.Ordinal) && d.EndsWith("|plain", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BootImageGetsTheHardwareBypassOnIndexTwo()
    {
        await RunAsync(Builder().CreateJob(Options()));

        var bootCalls = _fake.HiveActions.Where(a => a.StartsWith("boot:", StringComparison.Ordinal)).ToList();
        Assert.Contains(bootCalls, c => c.Contains("BypassTPMCheck", StringComparison.Ordinal));
        Assert.Contains(_fake.Calls, c => c == "mount:boot.wim:2");
    }

    [Fact]
    public async Task BootImageIsLeftAloneWhenTheBypassIsOff()
    {
        await RunAsync(Builder().CreateJob(Options() with { BypassHardwareChecks = false }));

        Assert.DoesNotContain(_fake.Calls, c => c.StartsWith("mount:boot.wim", StringComparison.Ordinal));
    }

    [Fact]
    public void CoreNeedsAnExplicitAcknowledgement()
    {
        var ex = Assert.Throws<BootrixException>(() => Builder().CreateJob(Options("tiny11core")));

        Assert.Equal(ErrorCode.InvalidSpec, ex.Code);
    }

    [Fact]
    public async Task CoreRebuildsWinSxsForTheImageArchitectureAndSkipsComponentCleanup()
    {
        _fake.Architecture = "arm64";
        var options = Options("tiny11core") with { AcknowledgeNoServicing = true };

        var result = await RunAsync(Builder().CreateJob(options));

        Assert.True(result.Succeeded, result.Error?.ToString());
        var keep = Assert.Single(_fake.WinSxsKeepLists);
        Assert.Contains("arm64_microsoft-windows-servicingstack-onecore_31bf3856ad364e35_*", keep);
        Assert.DoesNotContain(_fake.Calls, c => c == "cleanup-store");
        Assert.Contains(_fake.EmptiedFiles, f => f.EndsWith("winre.wim", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnknownArchitectureNeverWipesTheComponentStore()
    {
        _fake.Architecture = "riscv64";
        var options = Options("tiny11core") with { AcknowledgeNoServicing = true };

        await RunAsync(Builder().CreateJob(options));

        Assert.Empty(_fake.WinSxsKeepLists);
        Assert.Contains(_fake.Calls, c => c == "cleanup-store");
    }

    [Fact]
    public async Task LanguageFeaturePatternsUseTheImageLanguage()
    {
        _fake.Language = "de-DE";
        _fake.Packages =
        [
            new WindowsPackage("Microsoft-Windows-LanguageFeatures-OCR-de-DE-Package~31bf3856ad364e35~amd64~~10.0.1", "Installed"),
            new WindowsPackage("Microsoft-Windows-LanguageFeatures-OCR-en-US-Package~31bf3856ad364e35~amd64~~10.0.1", "Installed"),
            new WindowsPackage("Microsoft-Windows-Client-Features-Package~31bf3856ad364e35~amd64~~10.0.1", "Installed"),
        ];
        var options = Options("tiny11core") with { AcknowledgeNoServicing = true };

        await RunAsync(Builder().CreateJob(options));

        Assert.Equal(["Microsoft-Windows-LanguageFeatures-OCR-de-DE-Package~31bf3856ad364e35~amd64~~10.0.1"], _fake.RemovedPackages);
    }

    [Fact]
    public async Task OnlyInstalledCapabilitiesAreRemovedForTiny10()
    {
        _fake.Capabilities =
        [
            new WindowsCapability("Browser.InternetExplorer~~~~0.0.11.0", "Installed"),
            new WindowsCapability("MathRecognizer~~~~0.0.1.0", "NotPresent"),
            new WindowsCapability("OpenSSH.Client~~~~0.0.1.0", "Installed"),
        ];

        await RunAsync(Builder().CreateJob(Options("tiny10")));

        Assert.Equal(["Browser.InternetExplorer~~~~0.0.11.0"], _fake.RemovedCapabilities);
    }

    [Fact]
    public async Task FailureDiscardsTheMountedImageAndUnloadsHives()
    {
        _fake.FailOnHiveSet = true;

        var result = await RunAsync(Builder().CreateJob(Options()));

        Assert.Equal(JobOutcome.Failed, result.Outcome);
        Assert.Equal("Tiny.Registry", result.FailedStep);
        Assert.True(_fake.MountDisposed);
        Assert.DoesNotContain(_fake.Calls, c => c == "unmount:commit");
        Assert.Equal(_fake.HivesLoaded, _fake.HivesUnloaded);
    }

    [Fact]
    public async Task CancellationDuringServicingDiscardsTheImage()
    {
        using var cts = new CancellationTokenSource();
        _fake.OnRemoveAppx = () => cts.Cancel();
        _fake.Appx = [new ProvisionedAppx("Microsoft.BingNews_1", "News"), new ProvisionedAppx("Microsoft.Todos_1", "Todos")];

        var result = await RunAsync(Builder().CreateJob(Options()), cts.Token);

        Assert.Equal(JobOutcome.Canceled, result.Outcome);
        Assert.True(_fake.MountDisposed);
    }

    [Fact]
    public async Task NotEnoughSpaceStopsBeforeAnythingIsCopied()
    {
        _fake.FreeBytes = 1024;

        var result = await RunAsync(Builder().CreateJob(Options()));

        Assert.Equal(JobOutcome.Failed, result.Outcome);
        var error = Assert.IsType<BootrixException>(result.Error);
        Assert.Equal(ErrorCode.InsufficientSpace, error.Code);
        Assert.DoesNotContain(_fake.Calls, c => c == "copy-media");
    }

    [Fact]
    public async Task MissingInstallImageIsReported()
    {
        File.Delete(Path.Combine(_source, "sources", "install.wim"));

        var result = await RunAsync(Builder().CreateJob(Options()));

        Assert.Equal(ErrorCode.ImageUnsupported, Assert.IsType<BootrixException>(result.Error).Code);
    }

    [Fact]
    public async Task EsdSourcesAreAccepted()
    {
        File.Move(Path.Combine(_source, "sources", "install.wim"), Path.Combine(_source, "sources", "install.esd"));

        var result = await RunAsync(Builder().CreateJob(Options()));

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Contains(_fake.Calls, c => c.StartsWith("export:6->install.wim", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FinalCompressionFollowsTheOption()
    {
        await RunAsync(Builder().CreateJob(Options() with { Compression = InstallImageCompression.Recovery }));

        Assert.Contains(_fake.Calls, c => c == "export:1->install.wim.tmp:Recovery");
        Assert.Contains(_fake.Calls, c => c == "export:6->install.wim:Maximum");
    }

    [Fact]
    public async Task UnattendIsWrittenToTheMediaRoot()
    {
        var options = Options() with
        {
            Unattend = new UnattendOptions { Arch = WindowsArch.X64, Windows = new WindowsSetupOptions { LocalAccountName = "Kunde" } },
        };

        await RunAsync(Builder().CreateJob(options));

        var xml = await File.ReadAllTextAsync(Path.Combine(_work, "media", "autounattend.xml"));
        Assert.Contains("<Name>Kunde</Name>", xml, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("amd64", "amd64")]
    [InlineData("arm64", "arm64")]
    [InlineData("x86", "x86")]
    public async Task UnattendFollowsTheArchitectureOfTheExportedImage(string imageArchitecture, string expected)
    {
        _fake.Architecture = imageArchitecture;
        var options = Options() with
        {
            Unattend = new UnattendOptions { Arch = WindowsArch.X64, ImageName = "Windows 11 Pro", Windows = new WindowsSetupOptions { BypassTpm = true } },
        };

        await RunAsync(Builder().CreateJob(options));

        var xml = await File.ReadAllTextAsync(Path.Combine(_work, "media", "autounattend.xml"));
        Assert.Contains($"processorArchitecture=\"{expected}\"", xml, StringComparison.Ordinal);
        Assert.Contains("<Value>1</Value>", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("Windows 11 Pro", xml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IsoIsCreatedWhenRequested()
    {
        var iso = Path.Combine(_root, "tiny.iso");

        var result = await RunAsync(Builder().CreateJob(Options() with { IsoPath = iso, VolumeLabel = "TINY11" }));

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal([(iso, "TINY11")], _fake.IsoRequests);
    }

    [Fact]
    public void ResolvedProfileFiltersGroups()
    {
        var all = TinyBuilder.ResolveProfile(Options());
        var withoutXbox = TinyBuilder.ResolveProfile(Options() with { DisabledGroups = new HashSet<string> { "xbox" } });

        Assert.True(withoutXbox.Appx.Count < all.Appx.Count);
        Assert.DoesNotContain(withoutXbox.Appx, a => a.Name.StartsWith("Microsoft.Xbox", StringComparison.Ordinal));
    }

    private sealed class FakeServices : IImageServicing, IImageFileSystem, IInstallImageTools, IIsoWriter
    {
        public List<string> Calls { get; } = [];

        public List<string> RemovedAppx { get; } = [];

        public List<string> RemovedPackages { get; } = [];

        public List<string> RemovedCapabilities { get; } = [];

        public List<string> Deleted { get; } = [];

        public List<string> EmptiedFiles { get; } = [];

        public List<string> HiveActions { get; } = [];

        public List<IReadOnlyList<string>> WinSxsKeepLists { get; } = [];

        public List<(string Iso, string Label)> IsoRequests { get; } = [];

        public IReadOnlyList<ProvisionedAppx> Appx { get; set; } = [];

        public IReadOnlyList<WindowsPackage> Packages { get; set; } = [];

        public IReadOnlyList<WindowsCapability> Capabilities { get; set; } = [];

        public string Architecture { get; set; } = "amd64";

        public string Language { get; set; } = "en-US";

        public long FreeBytes { get; set; } = 100L << 30;

        public bool FailOnHiveSet { get; set; }

        public Action? OnRemoveAppx { get; set; }

        public bool MountDisposed { get; private set; }

        public int HivesLoaded { get; private set; }

        public int HivesUnloaded { get; private set; }

        private string _currentMountName = "";

        public Task<IMountedImage> MountAsync(string imagePath, int index, string mountDirectory, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            _currentMountName = Path.GetFileName(imagePath);
            Calls.Add($"mount:{_currentMountName}:{index}");
            return Task.FromResult<IMountedImage>(new FakeMount(this, mountDirectory));
        }

        public Task<IReadOnlyList<ProvisionedAppx>> GetProvisionedAppxAsync(string mountDirectory, CancellationToken cancellationToken)
        {
            Calls.Add("list-appx");
            return Task.FromResult(Appx);
        }

        public Task RemoveProvisionedAppxAsync(string mountDirectory, string packageName, CancellationToken cancellationToken)
        {
            if (!Calls.Contains("remove-appx"))
            {
                Calls.Add("remove-appx");
            }

            RemovedAppx.Add(packageName);
            OnRemoveAppx?.Invoke();
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WindowsPackage>> GetPackagesAsync(string mountDirectory, CancellationToken cancellationToken) => Task.FromResult(Packages);

        public Task RemovePackageAsync(string mountDirectory, string identity, CancellationToken cancellationToken)
        {
            RemovedPackages.Add(identity);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WindowsCapability>> GetCapabilitiesAsync(string mountDirectory, CancellationToken cancellationToken) => Task.FromResult(Capabilities);

        public Task RemoveCapabilityAsync(string mountDirectory, string name, CancellationToken cancellationToken)
        {
            RemovedCapabilities.Add(name);
            return Task.CompletedTask;
        }

        public Task CleanupComponentStoreAsync(string mountDirectory, CancellationToken cancellationToken)
        {
            Calls.Add("cleanup-store");
            return Task.CompletedTask;
        }

        public IOfflineHive LoadHive(string mountDirectory, RegistryHive hive)
        {
            HivesLoaded++;
            Calls.Add($"load-hive:{hive}");
            return new FakeHive(this, _currentMountName == "boot.wim");
        }

        public Task DeleteAsync(string path, bool takeOwnership, CancellationToken cancellationToken)
        {
            if (!Calls.Contains("delete-files"))
            {
                Calls.Add("delete-files");
            }

            Deleted.Add(path + (takeOwnership ? "|owner" : "|plain"));
            return Task.CompletedTask;
        }

        public Task ReplaceWithEmptyFileAsync(string path, CancellationToken cancellationToken)
        {
            EmptiedFiles.Add(path);
            return Task.CompletedTask;
        }

        public Task RebuildWinSxsAsync(string winSxsPath, IReadOnlyList<string> keepPatterns, CancellationToken cancellationToken)
        {
            WinSxsKeepLists.Add(keepPatterns);
            return Task.CompletedTask;
        }

        public long GetFreeBytes(string path) => FreeBytes;

        public Task CopyDirectoryAsync(string source, string destination, Func<string, bool>? filter, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            Calls.Add("copy-media");
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                if (filter is not null && !filter(file))
                {
                    continue;
                }

                var target = Path.Combine(destination, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<InstallEdition>> GetEditionsAsync(string installImagePath, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<InstallEdition>>([new InstallEdition(6, "Windows 11 Pro", null, 1)]);

        public Task ExportEditionAsync(string source, int index, string destination, InstallImageCompression compression, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            Calls.Add($"export:{index}->{Path.GetFileName(destination)}:{compression}");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination, "exported install image");
            return Task.CompletedTask;
        }

        public Task<string> GetArchitectureAsync(string imagePath, int index, CancellationToken cancellationToken)
        {
            Calls.Add("arch");
            return Task.FromResult(Architecture);
        }

        public Task<string> GetDefaultLanguageAsync(string imagePath, int index, CancellationToken cancellationToken) => Task.FromResult(Language);

        public Task CreateAsync(string mediaDirectory, string isoPath, string volumeLabel, bool uefi2023, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            IsoRequests.Add((isoPath, volumeLabel));
            return Task.CompletedTask;
        }

        private sealed class FakeMount(FakeServices owner, string directory) : IMountedImage
        {
            private bool _committed;

            public string MountDirectory => directory;

            public Task UnmountAsync(bool commit, CancellationToken cancellationToken)
            {
                owner.Calls.Add(commit ? "unmount:commit" : "unmount:discard");
                _committed = commit;
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                if (!_committed)
                {
                    owner.MountDisposed = true;
                }

                return ValueTask.CompletedTask;
            }
        }

        private sealed class FakeHive(FakeServices owner, bool boot) : IOfflineHive
        {
            public void SetValue(string key, string name, RegistryValueKind kind, string value)
            {
                if (owner.FailOnHiveSet)
                {
                    throw new IOException("hive is read-only");
                }

                owner.HiveActions.Add($"{(boot ? "boot:" : "")}set {key}\\{name}={value}");
            }

            public void DeleteKey(string key) => owner.HiveActions.Add($"{(boot ? "boot:" : "")}delete-key {key}");

            public void DeleteValue(string key, string name) => owner.HiveActions.Add($"{(boot ? "boot:" : "")}delete-value {key}\\{name}");

            public void Dispose() => owner.HivesUnloaded++;
        }
    }
}
