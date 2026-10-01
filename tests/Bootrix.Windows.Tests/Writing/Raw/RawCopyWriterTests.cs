// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Images;
using Bootrix.Core.Images.Disk;
using Bootrix.Core.Jobs;
using Bootrix.Core.Model;
using Bootrix.Core.Partitioning;
using DiskLayout = Bootrix.Core.Images.Disk.DiskLayout;
using Bootrix.Core.Planning;
using Bootrix.Core.Profiles;
using Bootrix.Core.Storage;
using Bootrix.Windows.Jobs;
using Bootrix.Windows.Writing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Windows.Tests.Writing.Raw;

/// <summary>The steps a raw copy is made of: the persistence partition comes after the verified image and before the final flush.</summary>
public sealed class RawCopyWriterTests : IDisposable
{
    private const long Mib = 1024 * 1024;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bootrix-rawcopy-" + Guid.NewGuid().ToString("N"));

    public RawCopyWriterTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

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

    private RawCopyWriter Writer() => new(new RawWriteJob(
        new NoDisks(),
        new FileImageStreamProvider(),
        new JobJournal(_directory),
        NullLogger<RawWriteJob>.Instance));

    private static StorageDevice Stick() => new()
    {
        DiskNumber = 3,
        DevicePath = @"\\?\usbstor#disk&ven_test",
        Serial = "S1",
        SizeBytes = 8L << 30,
        Bus = BusType.Usb,
        IsRemovableMedia = true,
    };

    private static MediaPlan Plan(bool persistence) => new()
    {
        WriteMethod = WriteMethod.RawCopy,
        BootMethod = BootMethod.ImageNative,
        DeviceBytes = 8L << 30,
        NeedsPersistencePartition = persistence,
        Partitions = persistence
            ?
            [
                new PlannedPartition
                {
                    Role = PartitionRole.Persistence,
                    StartBytes = 4 * Mib,
                    LengthBytes = 512 * Mib,
                    FileSystem = FileSystemKind.Ext3,
                    Label = "persistence",
                    MbrType = MbrPartitionType.Linux,
                },
            ]
            : [],
    };

    private static DiskLayout HybridIsoLayout(int mbrEntries) => new()
    {
        HasMbrSignature = true,
        HasBootCode = true,
        MbrPartitions = [.. Enumerable.Range(0, mbrEntries).Select(i => new MbrPartition(i, 0x83, i == 0, 2048 + (i * 4096), 2048))],
    };

    private MediaWriteContext Context(ImageKind kind, MediaPlan plan, DiskLayout? layout)
    {
        var device = Stick();
        return new MediaWriteContext
        {
            JobId = "write-test",
            ImagePath = Path.Combine(_directory, "image.iso"),
            Inspection = new ImageInspection
            {
                Profile = new ImageProfile { Kind = kind, Family = "debian-live" },
                Container = ImageContainer.Iso9660,
                Layout = layout,
            },
            Spec = new JobSpec(),
            Targets = [new MediaWriteTarget { Device = device, Identity = DiskIdentity.From(device), Plan = plan }],
            WorkDirectory = Path.Combine(_directory, "work"),
        };
    }

    private static string[] Keys(IReadOnlyList<IJobStep> steps) => [.. steps.Select(s => s.Key)];

    [Fact]
    public void WithoutPersistence_TheStepsAreTheOldFour()
    {
        var steps = Writer().CreateSteps(Context(ImageKind.LinuxHybrid, Plan(persistence: false), HybridIsoLayout(2)));

        Assert.Equal(["Raw.CheckTargets", "Raw.Prepare", "Raw.Write", "Raw.Finish"], Keys(steps));
    }

    [Fact]
    public void WithPersistence_TheStepSitsBetweenTheVerifiedWriteAndTheFinish()
    {
        var steps = Writer().CreateSteps(Context(ImageKind.LinuxHybrid, Plan(persistence: true), HybridIsoLayout(2)));

        Assert.Equal(["Raw.CheckTargets", "Raw.Prepare", "Raw.Write", "Raw.Persistence", "Raw.Finish"], Keys(steps));
    }

    [Fact]
    public void AFullMbr_IsRefusedBeforeAnythingCouldBeWritten()
    {
        var error = Assert.Throws<BootrixException>(() =>
            Writer().CreateSteps(Context(ImageKind.LinuxHybrid, Plan(persistence: true), HybridIsoLayout(4))));

        Assert.Equal(ErrorCode.PersistenceLayoutUnsupported, error.Code);
    }

    [Fact]
    public void AMacDiskWithAGpt_GetsItsBackupMovedAfterTheWrite()
    {
        var layout = new DiskLayout
        {
            HasMbrSignature = true,
            MbrPartitions = [new MbrPartition(0, 0xEE, false, 1, 4095)],
            HasGpt = true,
            GptHeaderValid = true,
            GptSectorSize = 512,
        };

        var steps = Writer().CreateSteps(Context(ImageKind.Apple, Plan(persistence: false), layout));

        Assert.Equal(["Raw.CheckTargets", "Raw.Prepare", "Raw.Write", "Raw.RelocateGpt", "Raw.Finish"], Keys(steps));
    }

    [Fact]
    public void AnApmDisk_NeedsNoGptWork()
    {
        var steps = Writer().CreateSteps(Context(ImageKind.Apple, Plan(persistence: false), new DiskLayout { HasApm = true }));

        Assert.DoesNotContain("Raw.RelocateGpt", Keys(steps));
    }

    [Fact]
    public void TheRequestForThePartitionComesFromThePlan()
    {
        var request = Writer().CreateSteps(Context(ImageKind.LinuxHybrid, Plan(persistence: true), HybridIsoLayout(1)));
        Assert.Contains("Raw.Persistence", Keys(request));

        var persistence = Core.Writing.Raw.PersistenceRequest.FromPlan(Plan(persistence: true).Partitions[0]);

        Assert.Equal("persistence", persistence.Label);
        Assert.Equal(4 * Mib, persistence.StartBytes);
        Assert.Equal(512 * Mib, persistence.LengthBytes);
        Assert.Equal("persistence.conf", Assert.Single(persistence.Files).Name);
    }

    [Fact]
    public void EveryStepOfTheRawJobHasAnUiText()
    {
        var steps = Writer().CreateSteps(Context(ImageKind.LinuxHybrid, Plan(persistence: true), HybridIsoLayout(1)));

        Assert.All(steps, step => Assert.True(Core.Localization.Localizer.Default.Has("Step." + step.Key), step.Key));
    }
}
