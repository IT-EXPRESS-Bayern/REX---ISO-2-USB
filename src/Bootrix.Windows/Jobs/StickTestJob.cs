// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Jobs;
using Bootrix.Core.Storage;
using Bootrix.Core.Storage.Testing;
using Bootrix.Windows.Interop;
using Bootrix.Windows.Platform;
using Bootrix.Windows.Storage;
using Microsoft.Extensions.Logging;

namespace Bootrix.Windows.Jobs;

public sealed record StickTestTarget(StorageDevice Device, DiskIdentity ConfirmedIdentity);

/// <summary>
/// Writes test data over sticks and reads it back, to find sticks with a faked capacity and sticks with bad blocks.
/// The data on the stick is gone afterwards; the tables are cleared at the end so that Windows does not try to read
/// a file system out of test patterns.
/// </summary>
public sealed class StickTestJob(IDiskService disks, ILogger<StickTestJob> logger)
{
    public const string ReportKey = "stick.report";

    public IJob Create(IReadOnlyList<StickTestTarget> targets, StickTestMode mode)
    {
        if (targets.Count == 0)
        {
            throw new ArgumentException("At least one target disk is required.", nameof(targets));
        }

        var run = new Run(disks, logger, targets, mode);
        var steps = new List<IJobStep>
        {
            new DelegateJobStep("Test.Check", 1, run.CheckAsync),
            new DelegateJobStep("Test.Capacity", mode == StickTestMode.Capacity ? 94 : 14, run.CapacityAsync),
        };
        if (mode != StickTestMode.Capacity)
        {
            steps.Add(new DelegateJobStep("Test.BadBlocks", 80, run.BadBlocksAsync));
        }

        steps.Add(new DelegateJobStep("Test.Finish", 5, run.FinishAsync));
        return new Job("stick-test-" + Guid.NewGuid().ToString("N")[..8], targets.Count == 1 ? targets[0].Device.DisplayName : $"{targets.Count} drives", steps);
    }

    private sealed class Run(IDiskService disks, ILogger logger, IReadOnlyList<StickTestTarget> targets, StickTestMode mode)
    {
        private readonly Dictionary<StickTestTarget, CapacityProbeResult> _capacity = [];
        private readonly Dictionary<StickTestTarget, BadBlockResult> _badBlocks = [];

        public Task CheckAsync(JobContext context, CancellationToken cancellationToken)
        {
            foreach (var target in targets)
            {
                var device = target.Device;
                if (device.IsBlocked)
                {
                    throw new BootrixException(ErrorCode.DeviceProtected, device.DevicePath) { Arguments = [device.Protection.ToString()] };
                }

                var mutex = DiskMutex.TryAcquire(device)
                    ?? throw new BootrixException(ErrorCode.DeviceBusy, device.DevicePath) { Arguments = ["Bootrix"] };
                context.OnCleanup(mutex);

                DiskIdentityReader.EnsureUnchanged(disks, device, target.ConfirmedIdentity);
                cancellationToken.ThrowIfCancellationRequested();
            }

            context.OnCleanup(new SleepGuard());
            return Task.CompletedTask;
        }

        public Task CapacityAsync(JobContext context, CancellationToken cancellationToken) =>
            RunEachAsync(context, async (target, progress) =>
            {
                using var locks = VolumeLockSet.Acquire(target.Device, logger, cancellationToken);
                using var disk = DiskAccess.Open(target.Device, write: true, cancellationToken);
                _capacity[target] = await CapacityProbe.RunAsync(disk, progress: progress, cancellationToken: cancellationToken).ConfigureAwait(false);
            });

        public Task BadBlocksAsync(JobContext context, CancellationToken cancellationToken)
        {
            var patterns = mode == StickTestMode.Thorough ? BadBlockTester.ThoroughPatterns : BadBlockTester.QuickPatterns;
            return RunEachAsync(context, async (target, progress) =>
            {
                // A stick that already failed the capacity check has no sense in a test of space it does not have.
                var claimed = target.Device.SizeBytes;
                var probe = _capacity.GetValueOrDefault(target);
                using var locks = VolumeLockSet.Acquire(target.Device, logger, cancellationToken);
                using var disk = DiskAccess.Open(target.Device, write: true, cancellationToken);
                _badBlocks[target] = await BadBlockTester.RunAsync(
                    new LimitedDevice(disk, probe is { IsGenuine: false, EstimatedRealBytes: { } real } ? Math.Min(real, claimed) : claimed),
                    patterns,
                    progress: progress,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            });
        }

        public async Task FinishAsync(JobContext context, CancellationToken cancellationToken)
        {
            await Task.Run(
                () =>
                {
                    foreach (var target in targets)
                    {
                        using var locks = VolumeLockSet.Acquire(target.Device, logger, cancellationToken);
                        using var disk = DiskAccess.Open(target.Device, write: true, cancellationToken);
                        DiskWiper.WipeTables(disk);
                        disk.Flush();
                        DeviceIo.TryControl(disk.Handle, Ioctl.DiskUpdateProperties);
                    }
                },
                cancellationToken).ConfigureAwait(false);

            var reports = targets
                .Select(t => new StickTestReport(t.Device.DisplayName, t.Device.Serial, mode, _capacity[t], _badBlocks.GetValueOrDefault(t)))
                .ToList();
            context.Set(ReportKey, reports);
        }

        private async Task RunEachAsync(JobContext context, Func<StickTestTarget, IProgress<double>, Task> body)
        {
            for (var i = 0; i < targets.Count; i++)
            {
                var index = i;
                var share = 1.0 / targets.Count;
                await body(targets[i], new Progress<double>(value => context.ReportStep((index + value) * share))).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Shows only the first part of a device, for testing the space a stick really has.</summary>
    private sealed class LimitedDevice(IBlockDevice inner, long length) : IBlockDevice
    {
        public string Name => inner.Name;

        public int SectorSize => inner.SectorSize;

        public long Length { get; } = length / inner.SectorSize * inner.SectorSize;

        public int BufferAlignment => inner.BufferAlignment;

        public void Write(long offset, ReadOnlySpan<byte> data) => inner.Write(offset, data);

        public int Read(long offset, Span<byte> buffer) => inner.Read(offset, buffer);

        public void Flush() => inner.Flush();

        public void Dispose()
        {
            // The disk belongs to the caller.
        }
    }
}
