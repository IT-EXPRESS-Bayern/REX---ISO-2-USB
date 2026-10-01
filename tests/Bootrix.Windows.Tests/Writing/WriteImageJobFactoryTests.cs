// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Engine;
using Bootrix.Core.Errors;
using Bootrix.Core.Hosting;
using Bootrix.Core.Images;
using Bootrix.Core.Jobs;
using Bootrix.Core.Planning;
using Bootrix.Core.Profiles;
using Bootrix.Core.Storage;
using Bootrix.Core.Writing;
using Bootrix.Windows.Jobs;
using Bootrix.Windows.Writing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Windows.Tests.Writing;

public sealed class WriteImageJobFactoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bootrix-write-" + Guid.NewGuid().ToString("N"));

    public WriteImageJobFactoryTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class FakeDisks(params StorageDevice[] devices) : IDiskService
    {
        public event EventHandler? DevicesChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyList<StorageDevice> Enumerate(DiskFilter filter) => devices;

        public StorageDevice? Find(string devicePath) => devices.FirstOrDefault(d => d.DevicePath == devicePath);
    }

    private sealed class FakeWriter(Func<MediaPlan, bool> accepts) : IMediaWriter
    {
        public MediaWriteContext? Seen { get; private set; }

        public string Id => "fake";

        public bool CanWrite(MediaPlan plan, ImageProfile image) => accepts(plan);

        public IReadOnlyList<IJobStep> CreateSteps(MediaWriteContext context)
        {
            Seen = context;
            return [new DelegateJobStep("Fake.Step", 1, (_, _) => Task.CompletedTask)];
        }
    }

    private static StorageDevice Stick(DeviceProtection protection = DeviceProtection.None) => new()
    {
        DiskNumber = 3,
        DevicePath = @"\\?\usbstor#disk&ven_test",
        Serial = "S123",
        SizeBytes = 16L << 30,
        Bus = BusType.Usb,
        IsRemovableMedia = true,
        Protection = protection,
    };

    /// <summary>A 2 MiB disk image with an MBR that holds one FAT32 partition: a plain raw image for the inspector.</summary>
    private string RawImage()
    {
        var path = Path.Combine(_directory, "disk.img");
        var data = new byte[2 << 20];
        var entry = 446;
        data[entry] = 0x80;
        data[entry + 4] = 0x0C;
        BitConverter.GetBytes(2048u).CopyTo(data, entry + 8);
        BitConverter.GetBytes(2048u).CopyTo(data, entry + 12);
        data[510] = 0x55;
        data[511] = 0xAA;
        File.WriteAllBytes(path, data);
        return path;
    }

    private WriteImageJobFactory Factory(IDiskService disks, params IMediaWriter[] writers) =>
        new(disks, new MediaPlanService(new ImageInspector()), new FileImageStreamProvider(), writers, BootrixPaths.ForPortable(_directory), NullLogger<WriteImageJobFactory>.Instance);

    private static WriteImageJobRequest Request(string image, StorageDevice device) => new()
    {
        ImagePath = image,
        Targets = [new EngineTarget(device.DevicePath, DiskIdentity.From(device))],
    };

    [Fact]
    public async Task TheFirstWriterThatAcceptsThePlanBuildsTheJob()
    {
        var device = Stick();
        var declining = new FakeWriter(_ => false);
        var accepting = new FakeWriter(_ => true);

        var job = await Factory(new FakeDisks(device), declining, accepting).CreateAsync(Request(RawImage(), device), CancellationToken.None);

        Assert.Equal(["Fake.Step"], job.Steps.Select(s => s.Key));
        Assert.Null(declining.Seen);
        Assert.NotNull(accepting.Seen);
        Assert.Equal("disk.img", job.Title);
    }

    [Fact]
    public async Task TheContextCarriesTheInspectionThePlanAndTheConfirmedIdentity()
    {
        var device = Stick();
        var writer = new FakeWriter(_ => true);

        await Factory(new FakeDisks(device), writer).CreateAsync(Request(RawImage(), device), CancellationToken.None);

        var context = writer.Seen!;
        var target = Assert.Single(context.Targets);
        Assert.Equal(device.DevicePath, target.Device.DevicePath);
        Assert.Equal("S123", target.Identity.Serial);
        Assert.Equal(16L << 30, target.Plan.DeviceBytes);
        Assert.NotEqual(ImageKind.Unknown, context.Image.Kind);
        Assert.StartsWith("write-", context.JobId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutAWriterTheError_NamesTheImageKindAndMethod()
    {
        var device = Stick();

        var ex = await Assert.ThrowsAsync<BootrixException>(() =>
            Factory(new FakeDisks(device), new FakeWriter(_ => false)).CreateAsync(Request(RawImage(), device), CancellationToken.None));

        Assert.Equal(ErrorCode.MediaWriterUnavailable, ex.Code);
        Assert.Equal(2, ex.Arguments.Count);
    }

    [Fact]
    public async Task ABlockedDiskIsRefusedBeforeAnythingIsPlanned()
    {
        var device = Stick(protection: DeviceProtection.SystemDisk);

        var ex = await Assert.ThrowsAsync<BootrixException>(() =>
            Factory(new FakeDisks(device), new FakeWriter(_ => true)).CreateAsync(Request(RawImage(), device), CancellationToken.None));

        Assert.Equal(ErrorCode.DeviceProtected, ex.Code);
    }

    [Fact]
    public async Task ADiskThatVanishedIsNotFound()
    {
        var device = Stick();

        var ex = await Assert.ThrowsAsync<BootrixException>(() =>
            Factory(new FakeDisks(), new FakeWriter(_ => true)).CreateAsync(Request(RawImage(), device), CancellationToken.None));

        Assert.Equal(ErrorCode.DeviceNotFound, ex.Code);
    }

    [Fact]
    public async Task AnUnreadableImageSurfacesAsAnIoError()
    {
        var device = Stick();

        await Assert.ThrowsAnyAsync<IOException>(() =>
            Factory(new FakeDisks(device), new FakeWriter(_ => true)).CreateAsync(Request(Path.Combine(_directory, "missing.iso"), device), CancellationToken.None));
    }

    [Fact]
    public async Task NoTargetsIsAProgrammingError()
    {
        var request = new WriteImageJobRequest { ImagePath = RawImage(), Targets = [] };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            Factory(new FakeDisks(), new FakeWriter(_ => true)).CreateAsync(request, CancellationToken.None));
    }

    [Fact]
    public void TheDefaultWriterListHandlesRawCopiesFirst()
    {
        var plan = new MediaPlan { WriteMethod = WriteMethod.RawCopy };
        var writer = new RawCopyWriter(null!);

        Assert.True(writer.CanWrite(plan, new ImageProfile()));
        Assert.False(writer.CanWrite(plan with { WriteMethod = WriteMethod.ExtractFiles }, new ImageProfile()));
    }
}
