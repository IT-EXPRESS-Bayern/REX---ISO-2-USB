// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using Bootrix.Core.Engine;
using Bootrix.Core.Ipc;
using Bootrix.Core.Jobs;
using Bootrix.Core.Storage;
using Bootrix.Core.Tests.Ipc;
using Microsoft.Extensions.Logging;

namespace Bootrix.Core.Tests.Engine;

internal static class TestPaths
{
    public const string Disk3 = @"\\?\usbstor#disk&ven_kingston&prod_datatraveler_3.0&rev_pmap#60a44c425f0ab2c0a000e06e&0#{53f56307-b6bf-11d0-94f2-00a0c91efb8b}";

    public const string Disk4 = @"\\?\usbstor#disk&ven_sandisk&prod_ultra&rev_1.00#4c530001010101#{53f56307-b6bf-11d0-94f2-00a0c91efb8b}";

    public const string Image = @"C:\images\ubuntu-24.04.iso";

    public static DiskIdentity IdentityOf(string devicePath) => new()
    {
        DevicePath = devicePath,
        DeviceGuid = "6f1b1d54-5d2b-4f55-9c3f-0f4c6a1c9d10",
        Serial = "60A44C425F0AB2C0A000E06E",
        SizeBytes = 16_008_609_792,
        PartitionSignature = "0x1234abcd",
        TableHash = "ab12",
    };

    public static RawWriteJobRequest ValidRequest(string image = Image, params string[] devices) => new()
    {
        ImagePath = image,
        Targets = [.. (devices.Length == 0 ? [Disk3] : devices).Select(d => new EngineTarget(d, IdentityOf(d)))],
    };

    public static StorageDevice SampleDevice(int number = 3, string path = Disk3) => new()
    {
        DiskNumber = number,
        DevicePath = path,
        Vendor = "Kingston",
        Product = "DataTraveler 3.0",
        Revision = "PMAP",
        Serial = "60A44C425F0AB2C0A000E06E",
        DeviceGuid = "6f1b1d54-5d2b-4f55-9c3f-0f4c6a1c9d10",
        Bus = BusType.Usb,
        SizeBytes = 16_008_609_792,
        LogicalSectorSize = 512,
        PhysicalSectorSize = 4096,
        IsRemovableMedia = true,
        PartitionStyle = DiskPartitionStyle.Mbr,
        PartitionSignature = "0x1234abcd",
        Partitions = [new ExistingPartition { Number = 1, Offset = 1_048_576, Length = 15_000_000_000, Type = "0x0c" }],
        Volumes =
        [
            new VolumeInfo
            {
                VolumeGuidPath = @"\\?\Volume{0f3c1c14-6f2e-4a89-8c63-aaaaaaaaaaaa}\",
                MountPoints = [@"E:\"],
                Label = "STICK",
                FileSystem = "FAT32",
                TotalBytes = 15_000_000_000,
                FreeBytes = 14_000_000_000,
                Extents = [new DiskExtent(number, 1_048_576, 15_000_000_000)],
            },
        ],
        Protection = DeviceProtection.WriteProtected | DeviceProtection.Offline,
    };
}

internal sealed class FakeEngine : IEngine
{
    public delegate Task<EngineJobResult> JobBehavior(EngineJobRequest request, IProgress<ProgressReport> progress, CancellationToken cancellationToken, CancellationToken abortToken);

    private int _jobsStarted;

    public event EventHandler? DevicesChanged;

    public IReadOnlyList<StorageDevice> Devices { get; set; } = [];

    public DiskFilter? LastFilter { get; private set; }

    public Func<string, Task<DiskIdentity>> Capture { get; set; } = path => Task.FromResult(TestPaths.IdentityOf(path));

    public JobBehavior Job { get; set; } = (_, _, _, _) => Task.FromResult(new EngineJobResult { Outcome = JobOutcome.Succeeded });

    public int JobsStarted => Volatile.Read(ref _jobsStarted);

    public void RaiseDevicesChanged() => DevicesChanged?.Invoke(this, EventArgs.Empty);

    public Task<IReadOnlyList<StorageDevice>> ListDisksAsync(DiskFilter filter, CancellationToken cancellationToken)
    {
        LastFilter = filter;
        return Task.FromResult(Devices);
    }

    public Task<DiskIdentity> CaptureIdentityAsync(string devicePath, CancellationToken cancellationToken) => Capture(devicePath);

    public Task<EngineJobResult> RunJobAsync(EngineJobRequest request, IProgress<ProgressReport> progress, CancellationToken cancellationToken, CancellationToken abortToken = default)
    {
        Interlocked.Increment(ref _jobsStarted);
        return Job(request, progress, cancellationToken, abortToken);
    }

    public static ProgressReport Report(long done, long total) =>
        new("job", 0, 1, "Raw.Write", (double)done / total, (double)done / total, done, total, 0, null, null);
}

/// <summary>Collects reports in the order they arrive; <see cref="Progress{T}"/> would hop threads and shuffle them.</summary>
internal sealed class SyncProgress : IProgress<ProgressReport>
{
    private readonly ConcurrentQueue<ProgressReport> _reports = new();

    public IReadOnlyList<ProgressReport> Reports => [.. _reports];

    public void Report(ProgressReport value) => _reports.Enqueue(value);
}

/// <summary>A started host on one end of a real pipe and a connected client on the other.</summary>
internal sealed class BrokerPair : IAsyncDisposable
{
    private BrokerPair(FakeEngine engine, RpcConnection serverConnection, BrokerEngineHost host, BrokerEngineClient client, Stream clientStream)
    {
        Engine = engine;
        ServerConnection = serverConnection;
        Host = host;
        Client = client;
        ClientStream = clientStream;
    }

    public FakeEngine Engine { get; }

    public RpcConnection ServerConnection { get; }

    public BrokerEngineHost Host { get; }

    public BrokerEngineClient Client { get; }

    /// <summary>The raw client end, to cut the connection the way a crashing process does.</summary>
    public Stream ClientStream { get; }

    public static async Task<BrokerPair> CreateAsync(
        FakeEngine? engine = null,
        BrokerEngineHostOptions? hostOptions = null,
        BrokerClientOptions? clientOptions = null,
        ILogger? logger = null)
    {
        engine ??= new FakeEngine();
        var (serverStream, clientStream) = await PipePair.CreateAsync();
        var serverConnection = new RpcConnection(serverStream, logger: logger);
        var host = new BrokerEngineHost(engine, serverConnection, hostOptions, logger);
        serverConnection.Start();

        var client = await BrokerEngineClient.ConnectAsync(clientStream, clientOptions, logger).Within();
        return new BrokerPair(engine, serverConnection, host, client, clientStream);
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await ServerConnection.DisposeAsync();
        Host.Dispose();
    }
}

/// <summary>Remembers everything that was logged, including exception text and structured values, so a test can search it.</summary>
internal sealed class CapturingLogger : ILogger
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyList<string> Lines => [.. _lines];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var text = formatter(state, exception) + " " + state;
        if (state is IEnumerable<KeyValuePair<string, object?>> values)
        {
            text += " " + string.Join(' ', values.Select(v => $"{v.Key}={v.Value}"));
        }

        _lines.Enqueue($"[{logLevel}] {text} {exception}");
    }
}
