// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Engine;
using Bootrix.Core.Errors;
using Bootrix.Core.Jobs;
using Bootrix.Core.Storage;
using Bootrix.Windows.Engine;
using Bootrix.Windows.Jobs;
using Bootrix.Windows.Tests.Broker;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Windows.Tests.Engine;

public class LocalEngineTests
{
    private sealed record UnsupportedRequest : EngineJobRequest;

    private sealed class Harness
    {
        public FakeDiskService Disks { get; } = new FakeDiskService().Add(TestDisks.Device(3, TestDisks.Disk3), TestDisks.Device(4, TestDisks.Disk4));

        public List<RawWriteRequest> Created { get; } = [];

        public Func<RawWriteRequest, IJob>? JobFactory { get; set; }

        public Func<StorageDevice, DiskIdentity> Capture { get; set; } = TestDisks.Identity;

        public LocalEngine Engine => new(
            Disks,
            new JobRunner(NullLogger<JobRunner>.Instance),
            request =>
            {
                Created.Add(request);
                return JobFactory?.Invoke(request) ?? OneStep((_, _) => Task.CompletedTask);
            },
            Capture);
    }

    private static Job OneStep(Func<JobContext, CancellationToken, Task> body) =>
        new Job("test-job", "test", [new DelegateJobStep("Test.Step", 1, body)]);

    private static StorageDevice Disk3 => TestDisks.Device(3, TestDisks.Disk3);

    private static StorageDevice Disk4 => TestDisks.Device(4, TestDisks.Disk4);

    [Fact]
    public async Task RawWrite_ResolvesTheDevicesItself_AndKeepsTheIdentitiesTheUserConfirmed()
    {
        var harness = new Harness();
        var request = TestDisks.Request(Disk3, Disk4) with { Verify = false };

        var result = await harness.Engine.RunJobAsync(request, new SyncProgress(), default);

        Assert.True(result.Succeeded);
        var created = Assert.Single(harness.Created);
        Assert.Equal(TestDisks.Image, created.ImagePath);
        Assert.False(created.Verify);
        Assert.Equal([TestDisks.Disk3, TestDisks.Disk4], created.Targets.Select(t => t.Device.DevicePath));
        Assert.Equal(request.Targets.Select(t => t.Identity), created.Targets.Select(t => t.ConfirmedIdentity));
        Assert.Same(harness.Disks.Find(TestDisks.Disk3), created.Targets[0].Device);
    }

    [Fact]
    public async Task RawWrite_SuccessCarriesTheHashAndTheSizeOfTheImage()
    {
        var harness = new Harness
        {
            JobFactory = _ => OneStep((context, _) =>
            {
                context.Set(RawWriteJob.ReportKey, new RawWriteReport(4_200_000_000, "ab12cd34", []));
                return Task.CompletedTask;
            }),
        };

        var result = await harness.Engine.RunJobAsync(TestDisks.Request(Disk3), new SyncProgress(), default);

        Assert.True(result.Succeeded);
        Assert.Equal("ab12cd34", result.ImageSha256);
        Assert.Equal(4_200_000_000, result.ImageBytes);
    }

    [Fact]
    public async Task RawWrite_FailureCarriesNoHashEvenWhenAReportExists()
    {
        var harness = new Harness
        {
            JobFactory = _ => OneStep((context, _) =>
            {
                context.Set(RawWriteJob.ReportKey, new RawWriteReport(1, "ab12cd34", []));
                throw new BootrixException(ErrorCode.VerifyMismatch, "offset 123");
            }),
        };

        var result = await harness.Engine.RunJobAsync(TestDisks.Request(Disk3), new SyncProgress(), default);

        Assert.Equal(JobOutcome.Failed, result.Outcome);
        Assert.Equal(ErrorCode.VerifyMismatch, result.ErrorCode);
        Assert.Equal("Test.Step", result.FailedStep);
        Assert.Null(result.ImageSha256);
    }

    [Fact]
    public async Task Failure_KeepsTheCodeAndArgumentsForTheMessage()
    {
        var harness = new Harness
        {
            JobFactory = _ => OneStep((_, _) => throw new BootrixException(ErrorCode.DeviceTooSmall, "x") { Arguments = ["8 GB", "4 GB"] }),
        };

        var result = await harness.Engine.RunJobAsync(TestDisks.Request(Disk3), new SyncProgress(), default);

        Assert.Equal(ErrorCode.DeviceTooSmall, result.ErrorCode);
        Assert.Equal(["8 GB", "4 GB"], result.ErrorArguments);
        Assert.Contains("8 GB", ErrorCatalog.Describe(result.ToException()!).Cause, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Progress_ReachesTheCaller()
    {
        var harness = new Harness
        {
            JobFactory = _ => OneStep((context, _) =>
            {
                context.ReportBytes(50, 100);
                return Task.CompletedTask;
            }),
        };
        var progress = new SyncProgress();

        await harness.Engine.RunJobAsync(TestDisks.Request(Disk3), progress, default);

        Assert.Contains(progress.Reports, r => r is { StepKey: "Test.Step", BytesDone: 50, BytesTotal: 100 });
    }

    [Fact]
    public async Task UnknownDevice_IsAFailedJobAndNothingStarts()
    {
        var harness = new Harness();
        var request = TestDisks.Request(Disk3, TestDisks.Device(9, @"\\?\usbstor#disk&ven_gone#9#{53f56307-b6bf-11d0-94f2-00a0c91efb8b}"));

        var result = await harness.Engine.RunJobAsync(request, new SyncProgress(), default);

        Assert.Equal(JobOutcome.Failed, result.Outcome);
        Assert.Equal(ErrorCode.DeviceNotFound, result.ErrorCode);
        Assert.Empty(harness.Created);
    }

    [Fact]
    public async Task RequestTypeWithoutAnEntry_IsRefused()
    {
        var harness = new Harness();

        var result = await harness.Engine.RunJobAsync(new UnsupportedRequest(), new SyncProgress(), default);

        Assert.Equal(JobOutcome.Failed, result.Outcome);
        Assert.Equal(ErrorCode.InvalidSpec, result.ErrorCode);
        Assert.Empty(harness.Created);
    }

    [Fact]
    public async Task SoftCancel_StopsTheJobAndReportsCancellation()
    {
        var started = new TaskCompletionSource();
        var harness = new Harness
        {
            JobFactory = _ => OneStep(async (_, token) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            }),
        };
        using var cts = new CancellationTokenSource();

        var run = harness.Engine.RunJobAsync(TestDisks.Request(Disk3), new SyncProgress(), cts.Token);
        await started.Task.Within();
        await cts.CancelAsync();

        Assert.Equal(JobOutcome.Canceled, (await run.Within()).Outcome);
    }

    [Fact]
    public async Task AlreadyCancelled_NeverBuildsTheJob()
    {
        var harness = new Harness();

        var result = await harness.Engine.RunJobAsync(TestDisks.Request(Disk3), new SyncProgress(), new CancellationToken(true));

        Assert.Equal(JobOutcome.Canceled, result.Outcome);
        Assert.Empty(harness.Created);
    }

    [Fact]
    public async Task AbortToken_ReachesTheStepsAndCleanupStillRuns()
    {
        var started = new TaskCompletionSource();
        var cleaned = false;
        var harness = new Harness
        {
            JobFactory = _ => OneStep(async (context, _) =>
            {
                context.OnCleanup(() => cleaned = true);
                started.SetResult();
                await Task.Delay(Timeout.Infinite, context.AbortToken);
            }),
        };
        using var abort = new CancellationTokenSource();

        var run = harness.Engine.RunJobAsync(TestDisks.Request(Disk3), new SyncProgress(), default, abort.Token);
        await started.Task.Within();
        await abort.CancelAsync();

        Assert.Equal(JobOutcome.Canceled, (await run.Within()).Outcome);
        Assert.True(cleaned);
    }

    [Fact]
    public async Task CaptureIdentity_FingerprintsTheDeviceTheEngineFound()
    {
        var harness = new Harness();
        StorageDevice? seen = null;
        harness.Capture = device =>
        {
            seen = device;
            return TestDisks.Identity(device);
        };

        var identity = await harness.Engine.CaptureIdentityAsync(TestDisks.Disk4, default);

        Assert.Same(harness.Disks.Find(TestDisks.Disk4), seen);
        Assert.Equal("tablehash4", identity.TableHash);
        Assert.Equal(TestDisks.Disk4, identity.DevicePath);
    }

    [Fact]
    public async Task CaptureIdentity_UnknownDevice_ThrowsDeviceNotFound()
    {
        var harness = new Harness();

        var ex = await Assert.ThrowsAsync<BootrixException>(() => harness.Engine.CaptureIdentityAsync(@"\\?\usbstor#nothing#{53f56307-b6bf-11d0-94f2-00a0c91efb8b}", default));

        Assert.Equal(ErrorCode.DeviceNotFound, ex.Code);
    }

    [Fact]
    public async Task ListDisks_PassesTheFilterAndReturnsWhatTheServiceFinds()
    {
        var harness = new Harness();
        var filter = new DiskFilter { IncludeInternalDisks = true, IncludeBlocked = false };

        var disks = await harness.Engine.ListDisksAsync(filter, default);

        Assert.Equal([3, 4], disks.Select(d => d.DiskNumber));
        Assert.Same(filter, harness.Disks.LastFilter);
    }

    [Fact]
    public void DevicesChanged_IsTheEventOfTheDiskService()
    {
        var harness = new Harness();
        var engine = harness.Engine;
        var raised = 0;
        EventHandler handler = (_, _) => raised++;

        engine.DevicesChanged += handler;
        harness.Disks.Raise();
        engine.DevicesChanged -= handler;
        harness.Disks.Raise();

        Assert.Equal(1, raised);
        Assert.False(harness.Disks.HasSubscribers);
    }

    [Fact]
    public async Task BlockedDisks_AreHandedToTheJobUnchanged_SoThatTheJobCanRefuseThem()
    {
        var harness = new Harness();
        harness.Disks.Add(TestDisks.Device(0, TestDisks.Disk3, DeviceProtection.SystemDisk));

        await harness.Engine.RunJobAsync(TestDisks.Request(TestDisks.Device(0, TestDisks.Disk3, DeviceProtection.SystemDisk)), new SyncProgress(), default);

        Assert.True(Assert.Single(harness.Created).Targets[0].Device.IsBlocked);
    }
}
