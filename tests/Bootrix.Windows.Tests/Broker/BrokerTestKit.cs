// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using Bootrix.Core.Engine;
using Bootrix.Core.Jobs;
using Bootrix.Core.Storage;

namespace Bootrix.Windows.Tests.Broker;

internal static class TestDisks
{
    public const string Disk3 = @"\\?\usbstor#disk&ven_kingston&prod_datatraveler_3.0&rev_pmap#60a44c425f0ab2c0a000e06e&0#{53f56307-b6bf-11d0-94f2-00a0c91efb8b}";

    public const string Disk4 = @"\\?\usbstor#disk&ven_sandisk&prod_ultra&rev_1.00#4c530001010101#{53f56307-b6bf-11d0-94f2-00a0c91efb8b}";

    public const string Image = @"C:\images\ubuntu-24.04.iso";

    public static StorageDevice Device(int number, string path, DeviceProtection protection = DeviceProtection.None) => new()
    {
        DiskNumber = number,
        DevicePath = path,
        Vendor = "Kingston",
        Product = "DataTraveler",
        Serial = "SERIAL" + number,
        SizeBytes = 16_000_000_000,
        Bus = BusType.Usb,
        IsRemovableMedia = true,
        Protection = protection,
    };

    public static DiskIdentity Identity(StorageDevice device) => DiskIdentity.From(device, "tablehash" + device.DiskNumber);

    public static RawWriteJobRequest Request(params StorageDevice[] devices) => new()
    {
        ImagePath = Image,
        Targets = [.. devices.Select(d => new EngineTarget(d.DevicePath, Identity(d)))],
    };
}

internal sealed class FakeDiskService : IDiskService
{
    private readonly Dictionary<string, StorageDevice> _devices = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler? DevicesChanged;

    public DiskFilter? LastFilter { get; private set; }

    public FakeDiskService Add(params StorageDevice[] devices)
    {
        foreach (var device in devices)
        {
            _devices[device.DevicePath] = device;
        }

        return this;
    }

    public void Raise() => DevicesChanged?.Invoke(this, EventArgs.Empty);

    public bool HasSubscribers => DevicesChanged is not null;

    public IReadOnlyList<StorageDevice> Enumerate(DiskFilter filter)
    {
        LastFilter = filter;
        return [.. _devices.Values.OrderBy(d => d.DiskNumber)];
    }

    public StorageDevice? Find(string devicePath) => _devices.GetValueOrDefault(devicePath);
}

internal sealed class SyncProgress : IProgress<ProgressReport>
{
    private readonly ConcurrentQueue<ProgressReport> _reports = new();

    public IReadOnlyList<ProgressReport> Reports => [.. _reports];

    public void Report(ProgressReport value) => _reports.Enqueue(value);
}

internal sealed class FakeEngine : IEngine
{
    public delegate Task<EngineJobResult> JobBehavior(EngineJobRequest request, IProgress<ProgressReport> progress, CancellationToken cancellationToken, CancellationToken abortToken);

    private int _jobsStarted;

    public event EventHandler? DevicesChanged;

    public IReadOnlyList<StorageDevice> Devices { get; set; } = [];

    public JobBehavior Job { get; set; } = (_, _, _, _) => Task.FromResult(new EngineJobResult { Outcome = JobOutcome.Succeeded });

    public int JobsStarted => Volatile.Read(ref _jobsStarted);

    public int Identities { get; private set; }

    public void Raise() => DevicesChanged?.Invoke(this, EventArgs.Empty);

    public Task<IReadOnlyList<StorageDevice>> ListDisksAsync(DiskFilter filter, CancellationToken cancellationToken) => Task.FromResult(Devices);

    public Task<DiskIdentity> CaptureIdentityAsync(string devicePath, CancellationToken cancellationToken)
    {
        Identities++;
        return Task.FromResult(new DiskIdentity { DevicePath = devicePath, SizeBytes = 1 });
    }

    public Task<EngineJobResult> RunJobAsync(EngineJobRequest request, IProgress<ProgressReport> progress, CancellationToken cancellationToken, CancellationToken abortToken = default)
    {
        Interlocked.Increment(ref _jobsStarted);
        return Job(request, progress, cancellationToken, abortToken);
    }
}

internal static class Waiting
{
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(15);

    public static Task<T> Within<T>(this Task<T> task) => task.WaitAsync(Limit);

    public static Task Within(this Task task) => task.WaitAsync(Limit);
}
