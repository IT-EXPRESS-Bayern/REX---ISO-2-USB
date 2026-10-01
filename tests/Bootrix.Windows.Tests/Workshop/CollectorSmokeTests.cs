// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Storage;
using Bootrix.Core.Workshop;
using Bootrix.Core.Workshop.Capture;
using Bootrix.Windows.Workshop;

namespace Bootrix.Windows.Tests.Workshop;

/// <summary>
/// Runs the readers on the machine the tests run on. Hardware differs, so these tests only assert what is true on every Windows
/// machine and that the result can be turned into advice and JSON without leaking a key.
/// </summary>
public class CollectorSmokeTests
{
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

    [Fact]
    public async Task Collect_FillsTheSectionsEveryWindowsMachineHas()
    {
        var info = await new TargetPcCollector(new NoDisks()).CollectAsync();

        Assert.NotNull(info.CollectedAt);
        Assert.NotNull(info.IsElevated);
        Assert.NotNull(info.Cpu);
        Assert.True(info.Cpu.LogicalProcessors > 0);
        Assert.NotNull(info.Memory);
        Assert.True(info.Memory.UsableBytes > 0);
        Assert.NotNull(info.RunningSystem);
        Assert.True(info.RunningSystem.BuildNumber >= 17763);
        Assert.NotNull(info.Firmware);
        Assert.Empty(info.Disks!);
    }

    [Fact]
    public async Task Collect_ResultFeedsTheAdvisorAndSerializesWithoutSecrets()
    {
        var info = await new TargetPcCollector(new NoDisks()).CollectAsync();

        var json = TargetPcReport.Create(info).ToJson();

        Assert.False(string.IsNullOrWhiteSpace(json));
        Assert.DoesNotContain("plainKey", json, StringComparison.OrdinalIgnoreCase);
        if (info.OemLicense?.PlainKey is { } key)
        {
            Assert.DoesNotContain(key, json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Collect_CancelledBeforeItStarts_Throws()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TargetPcCollector(new NoDisks()).CollectAsync(cancellation.Token));
    }

    [Fact]
    public async Task Capture_WithDefaultOptions_ReadsNoSecret()
    {
        var capture = await new CustomerPcCaptureService().CaptureAsync(new CustomerPcCaptureOptions());

        Assert.False(capture.ContainsSecrets);
        Assert.NotNull(capture.CapturedAt);
        Assert.Null(capture.BitLockerRecoveryKeys);
        Assert.Null(capture.WindowsProductKey);
        Assert.NotNull(capture.InstalledPrograms);
        Assert.NotNull(capture.ThirdPartyDrivers);
        Assert.All(capture.WlanProfiles ?? [], profile => Assert.Null(profile.Key));
        Assert.All(capture.WlanProfiles ?? [], profile => Assert.Null(profile.ExportedFile));
    }

    [Fact]
    public async Task Capture_WithEverythingSwitchedOff_ReturnsAnEmptyInventory()
    {
        var options = new CustomerPcCaptureOptions
        {
            IncludeInstalledPrograms = false,
            IncludeThirdPartyDrivers = false,
            ListWlanProfiles = false,
        };

        var capture = await new CustomerPcCaptureService().CaptureAsync(options);

        Assert.Null(capture.WlanProfiles);
        Assert.Null(capture.InstalledPrograms);
        Assert.Null(capture.ThirdPartyDrivers);
        Assert.Empty(capture.Notes);
    }

    [Fact]
    public async Task Capture_ProductKeyOptIn_ReturnsTheKeyOnlyInTheSensitiveField()
    {
        var capture = await new CustomerPcCaptureService().CaptureAsync(new CustomerPcCaptureOptions { IncludeWindowsProductKey = true });

        if (capture.WindowsProductKey is not { } key)
        {
            return;
        }

        Assert.True(Bootrix.Core.Workshop.Licensing.ProductKeys.IsValidFormat(key.PlainKey));
        Assert.DoesNotContain(key.PlainKey!, WorkshopJson.Serialize(capture), StringComparison.Ordinal);
        Assert.DoesNotContain(key.PlainKey!, capture.ToString(), StringComparison.Ordinal);
        Assert.Contains(capture.Notes, note => note.Key == Bootrix.Core.Workshop.Advice.AdvisorKeys.CaptureSecretsIncluded);
    }
}
