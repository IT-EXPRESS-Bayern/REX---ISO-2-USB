// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Jobs;
using Bootrix.Core.Localization;
using Bootrix.Core.Model;
using Bootrix.Core.Planning;
using Bootrix.Core.Storage;
using Bootrix.Core.Writing.Restore;
using Bootrix.Windows.Engine;
using Bootrix.Windows.Jobs;
using Bootrix.Windows.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Windows.Tests.Writing.Raw;

/// <summary>The shape of the two jobs that need no media writer; their steps touch disks and so only run on Windows.</summary>
public sealed class RestoreAndVerifyJobTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "bootrix-jobs-" + Guid.NewGuid().ToString("N"));

    public RestoreAndVerifyJobTests() => Directory.CreateDirectory(_directory);

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

    private static StorageDevice Stick() => new()
    {
        DiskNumber = 5,
        DevicePath = @"\\?\usbstor#disk&ven_test",
        Serial = "S9",
        SizeBytes = 8L << 30,
        Bus = BusType.Usb,
        IsRemovableMedia = true,
    };

    private RestoreDriveJob Restore() =>
        new(new NoDisks(), new DiskPreparer(), new JobJournal(_directory), NullLogger<RestoreDriveJob>.Instance);

    [Fact]
    public void RestoreJob_HasTheFourStepsInOrder_AndEveryStepHasAText()
    {
        var device = Stick();
        var plan = RestorePlanner.Plan(new RestoreOptions { FileSystem = FileSystemKind.Fat32 }, MediaPlanServiceCaps(device));

        var job = Restore().Create([new RestoreTargetRequest(device, DiskIdentity.From(device), plan)]);

        Assert.Equal(["Restore.Check", "Restore.Wipe", "Restore.Format", "Restore.Finish"], job.Steps.Select(s => s.Key));
        Assert.All(job.Steps, step => Assert.True(Localizer.Default.Has("Step." + step.Key), step.Key));
        Assert.StartsWith("restore-", job.Id, StringComparison.Ordinal);
    }

    [Fact]
    public void RestoreJob_WithoutTargets_IsAProgrammingError()
    {
        Assert.Throws<ArgumentException>(() => Restore().Create([]));
    }

    [Fact]
    public void VerifyJob_HasTwoSteps_AndEveryStepHasAText()
    {
        var device = Stick();
        var job = new VerifyMediaJob(new NoDisks(), new FileImageStreamProvider(), NullLogger<VerifyMediaJob>.Instance)
            .Create(new VerifyMediaRequest { ImagePath = Path.Combine(_directory, "a.iso"), Targets = [new VerifyTargetRequest(device, DiskIdentity.From(device))] });

        Assert.Equal(["Verify.Check", "Verify.Compare"], job.Steps.Select(s => s.Key));
        Assert.All(job.Steps, step => Assert.True(Localizer.Default.Has("Step." + step.Key), step.Key));
    }

    [Fact]
    public void VerifyResult_CarriesTheComparedBytes()
    {
        var result = new JobResult(JobOutcome.Succeeded, TimeSpan.FromSeconds(2)) { Values = new Dictionary<string, object?> { [VerifyMediaJob.BytesKey] = 12345L } };

        var summary = LocalEngine.SummarizeVerify(result);

        Assert.Equal(12345, summary.ImageBytes);
        Assert.True(summary.Succeeded);
    }

    private static DeviceCaps MediaPlanServiceCaps(StorageDevice device) => Core.Writing.MediaPlanService.CapsOf(device);
}
