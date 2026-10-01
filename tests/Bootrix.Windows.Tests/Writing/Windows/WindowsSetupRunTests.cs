// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.FileSystems.Fat;
using Bootrix.Core.Images;
using Bootrix.Core.Jobs;
using Bootrix.Core.Model;
using Bootrix.Core.Planning;
using Bootrix.Core.Profiles;
using Bootrix.Core.Writing.Windows;
using Bootrix.Windows.Jobs;
using Bootrix.Windows.Storage;
using Bootrix.Windows.Writing;
using Bootrix.Windows.Writing.Windows;
using Microsoft.Extensions.Logging;

namespace Bootrix.Windows.Tests.Writing.Windows;

/// <summary>
/// Runs the steps of the writer through the real job runner on folders that stand in for the volumes. Only the
/// partitioning and the disk-level operations are replaced; the source, the plan, the copy, the hashes and the
/// read-back are the real ones. On Windows the ISO is attached; elsewhere the files are read from the image stream.
/// </summary>
public sealed class WindowsSetupRunTests : IDisposable
{
    private readonly WindowsWriteKit _kit = new();

    public void Dispose() => _kit.Dispose();

    private sealed record Arrangement(
        MediaWriteContext Context,
        WindowsSetupRun Run,
        FakeTargetOps Ops,
        FakeBootCode BootCode,
        Dictionary<string, byte[]> Files,
        IReadOnlyList<MediaWriteTarget> Targets);

    private async Task<Arrangement> ArrangeAsync(
        TargetOptions? options = null,
        int targets = 1,
        bool readBack = true,
        long deviceBytes = 128 * WindowsWriteKit.Mib,
        int sectorSize = 512,
        Dictionary<string, byte[]>? files = null,
        IReadOnlyList<IWindowsMediaCustomizer>? customizers = null,
        ILoggerFactory? loggers = null,
        Func<ImageInspection, ImageInspection>? inspect = null,
        WindowsArch arch = WindowsArch.X64)
    {
        files ??= WindowsWriteKit.SetupFiles();
        var (iso, inspection) = await _kit.BuildIsoAsync(files);
        var plan = WindowsWriteKit.Plan(options ?? new TargetOptions { Firmware = TargetFirmware.Uefi }, deviceBytes, sectorSize, WindowsWriteKit.Image(arch));
        var list = Enumerable.Range(0, targets).Select(i => _kit.Target(plan, 3 + i)).ToList();
        var context = _kit.Context(iso, inspect?.Invoke(inspection) ?? inspection, list, new JobSpec { Verify = new VerifyOptions { ReadBack = readBack } });
        var ops = new FakeTargetOps();
        var bootCode = new FakeBootCode();
        var writer = new WindowsSetupWriter(WindowsWriteKit.Services(loggers), new FileImageStreamProvider(), customizers ?? [], bootCode, ops);
        return new Arrangement(context, writer.CreateRun(context), ops, bootCode, files, list);
    }

    /// <summary>The steps of the writer, except that the partitioning is replaced by what it leaves behind: formatted volumes (folders here).</summary>
    private static List<IJobStep> Steps(Arrangement a, Action<WindowsSetupRun>? afterPrepare = null)
    {
        var steps = new List<IJobStep>
        {
            new DelegateJobStep(WindowsSetupWriter.SourceKey, 3, a.Run.OpenSourceAsync),
            new DelegateJobStep("Test.Prepare", 1, (_, _) =>
            {
                foreach (var target in a.Targets)
                {
                    foreach (var payload in PlanLayout.FatPayloads(target.Plan, a.Run.CustomizeFat))
                    {
                        var partition = target.Plan.Partitions[payload.PartitionIndex];
                        using var stream = new MemoryStream(new byte[partition.LengthBytes]);
                        payload.Write(stream);
                    }
                }

                afterPrepare?.Invoke(a.Run);
                return Task.CompletedTask;
            }),
            new DelegateJobStep(WindowsSetupWriter.CopyKey, 60, a.Run.CopyAsync),
        };

        if (a.Targets.Any(target => WindowsMbr.IsNeeded(target.Plan)))
        {
            steps.Add(new DelegateJobStep(WindowsSetupWriter.BootCodeKey, 1, a.Run.WriteBootCodeAsync));
        }

        if (a.Context.Spec.Verify.ReadBack)
        {
            steps.Add(new DelegateJobStep(WindowsSetupWriter.VerifyKey, 25, a.Run.VerifyAsync));
        }

        if (a.Run.Customizers.Count > 0)
        {
            steps.Add(new DelegateJobStep(WindowsSetupWriter.CustomizeKey, 5, a.Run.CustomizeAsync));
        }

        return steps;
    }

    private static void AssertMediumHolds(string root, Dictionary<string, byte[]> files)
    {
        foreach (var (path, content) in files)
        {
            Assert.Equal(content, File.ReadAllBytes(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar))));
        }
    }

    [RequiresXorrisoFact]
    public async Task AUefiMedium_ReceivesEveryFile_AndPassesTheReadBack()
    {
        var a = await ArrangeAsync();

        var (result, _) = await WindowsWriteKit.RunAsync(Steps(a));

        Assert.True(result.Outcome == JobOutcome.Succeeded, result.Error?.ToString());
        AssertMediumHolds(_kit.MainRoot(a.Targets[0]), a.Files);
        Assert.Equal(["flush", "drop-caches"], a.Ops.Calls);
        Assert.Equal(_kit.MainRoot(a.Targets[0]), Assert.Single(a.Ops.FlushedVolumes));
    }

    [RequiresXorrisoFact]
    public async Task TheCopyReportsBytes_ThatNeverGoBackAndEndAtTheTotal()
    {
        var a = await ArrangeAsync(readBack: false);

        var (result, reports) = await WindowsWriteKit.RunAsync(Steps(a));

        Assert.Equal(JobOutcome.Succeeded, result.Outcome);
        var copy = reports.Where(r => r.StepKey == WindowsSetupWriter.CopyKey && r.BytesTotal > 0).ToList();
        Assert.NotEmpty(copy);
        Assert.Equal(copy.Select(r => r.BytesDone).Order(), copy.Select(r => r.BytesDone));
        Assert.Equal(a.Files.Values.Sum(content => (long)content.Length), copy[^1].BytesTotal);
        Assert.Equal(copy[^1].BytesTotal, copy[^1].BytesDone);
        Assert.All(reports, r => Assert.InRange(r.OverallFraction, 0, 1));
    }

    [RequiresXorrisoFact]
    public async Task ADamagedFile_IsFoundByTheReadBack_AndNamedInTheError()
    {
        var a = await ArrangeAsync();
        a.Ops.OnDropCaches = target =>
        {
            var path = Path.Combine(_kit.MainRoot(target), "sources", "boot.wim");
            var content = File.ReadAllBytes(path);
            content[1000] ^= 0x01;
            File.WriteAllBytes(path, content);
        };

        var (result, _) = await WindowsWriteKit.RunAsync(Steps(a));

        Assert.Equal(JobOutcome.Failed, result.Outcome);
        var error = Assert.IsType<BootrixException>(result.Error);
        Assert.Equal(ErrorCode.MediaFileMismatch, error.Code);
        Assert.Equal(["sources/boot.wim"], error.Arguments);
        Assert.Equal(WindowsSetupWriter.VerifyKey, result.FailedStep);
    }

    [RequiresXorrisoFact]
    public async Task WithoutReadBack_NothingIsHashedOrDismounted()
    {
        var a = await ArrangeAsync(readBack: false);

        var (result, _) = await WindowsWriteKit.RunAsync(Steps(a));

        Assert.Equal(JobOutcome.Succeeded, result.Outcome);
        Assert.Equal(["flush"], a.Ops.Calls);
    }

    [RequiresXorrisoFact]
    public async Task ABiosMediumOnFat32_TakesItsBootSectorsFromTheSource_AndWritesTheMbr()
    {
        var a = await ArrangeAsync(new TargetOptions { Firmware = TargetFirmware.BiosAndUefi });
        FatFormatOptions? applied = null;
        FatFormatOptions? other = null;

        var (result, _) = await WindowsWriteKit.RunAsync(Steps(a, run =>
        {
            var plan = a.Targets[0].Plan;
            var main = plan.Partitions.Single(p => p.Role == PartitionRole.Main);
            applied = run.CustomizeFat(main, plan.ToFatOptions(main));
            other = run.CustomizeFat(main with { Role = PartitionRole.Esp }, plan.ToFatOptions(main));
        }));

        Assert.True(result.Outcome == JobOutcome.Succeeded, result.Error?.ToString());
        Assert.Equal(1, a.BootCode.Requests);
        Assert.Equal(new byte[13 * 512], applied!.BootCode);
        Assert.Null(other!.BootCode);
        Assert.Equal(["flush", "write-mbr", "drop-caches", "verify-mbr", "verify-boot-sectors"], a.Ops.Calls);
        Assert.Equal(13, a.Ops.VerifiedBootSectors!.SectorCount);
    }

    [RequiresXorrisoFact]
    public async Task TwoBiosTargets_AskForTheBootSectorsOnlyOnce()
    {
        var a = await ArrangeAsync(new TargetOptions { Firmware = TargetFirmware.Bios }, targets: 2);

        var (result, _) = await WindowsWriteKit.RunAsync(Steps(a));

        Assert.Equal(JobOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, a.BootCode.Requests);
        Assert.Equal(2, a.Ops.Calls.Count(call => call == "write-mbr"));
        Assert.Equal(2, a.Ops.Calls.Count(call => call == "verify-mbr"));
    }

    [RequiresXorrisoFact]
    public async Task ABiosMediumOnNtfs_NeedsNoBootSectorsFromWindows()
    {
        var a = await ArrangeAsync(new TargetOptions { Firmware = TargetFirmware.Bios, FileSystem = FileSystemKind.Ntfs });

        var (result, _) = await WindowsWriteKit.RunAsync(Steps(a));

        Assert.Equal(JobOutcome.Succeeded, result.Outcome);
        Assert.Equal(0, a.BootCode.Requests);
        Assert.Contains("write-mbr", a.Ops.Calls);
        Assert.DoesNotContain("verify-boot-sectors", a.Ops.Calls);
    }

    [RequiresXorrisoFact]
    public async Task OnFourKibSectors_ThereIsNoBiosBootCodeToFetch()
    {
        var a = await ArrangeAsync(new TargetOptions { Firmware = TargetFirmware.BiosAndUefi }, deviceBytes: 1024 * WindowsWriteKit.Mib, sectorSize: 4096);

        var (result, _) = await WindowsWriteKit.RunAsync(Steps(a));

        Assert.True(result.Outcome == JobOutcome.Succeeded, result.Error?.ToString());
        Assert.Equal(0, a.BootCode.Requests);
        Assert.DoesNotContain("verify-boot-sectors", a.Ops.Calls);
    }

    [RequiresXorrisoFact]
    public async Task AUefiOnlyMedium_NeedsNeitherMbrCodeNorBootSectors()
    {
        var a = await ArrangeAsync(new TargetOptions { Firmware = TargetFirmware.Uefi });

        var (result, _) = await WindowsWriteKit.RunAsync(Steps(a));

        Assert.Equal(JobOutcome.Succeeded, result.Outcome);
        Assert.Equal(0, a.BootCode.Requests);
        Assert.DoesNotContain("write-mbr", a.Ops.Calls);
        Assert.DoesNotContain("verify-mbr", a.Ops.Calls);
    }

    [RequiresXorrisoFact]
    public async Task TheUefiNtfsHelper_IsWrittenAsAPayload_AndReadBackAgainstTheSameBytes()
    {
        var a = await ArrangeAsync(new TargetOptions { Firmware = TargetFirmware.Uefi, FileSystem = FileSystemKind.Ntfs }, deviceBytes: 300 * WindowsWriteKit.Mib);
        var plan = a.Targets[0].Plan;
        Assert.True(plan.UsesUefiNtfs);
        var helperIndex = plan.Partitions.ToList().FindIndex(p => p.Role == PartitionRole.UefiNtfs);

        var payload = Assert.Single(a.Run.ExtraPayloads(a.Targets[0]));
        using var helper = new MemoryStream(new byte[plan.Partitions[helperIndex].LengthBytes]);
        payload.Write(helper);

        Assert.Equal(helperIndex, payload.PartitionIndex);
        Assert.Equal(UefiNtfsImage.ForSectorSize(512), helper.ToArray());

        var (result, _) = await WindowsWriteKit.RunAsync(Steps(a));

        Assert.Equal(JobOutcome.Succeeded, result.Outcome);
        var (partition, expected) = Assert.Single(a.Ops.VerifiedPartitions);
        Assert.Equal(PartitionRole.UefiNtfs, partition.Role);
        Assert.Equal(helper.ToArray(), expected);
    }

    [RequiresXorrisoFact]
    public async Task OnFourKibSectors_TheUefiNtfsHelperIsRebuiltOnceAndReadBackAgainstThoseBytes()
    {
        var a = await ArrangeAsync(
            new TargetOptions { Firmware = TargetFirmware.Uefi, FileSystem = FileSystemKind.Ntfs },
            deviceBytes: 1024 * WindowsWriteKit.Mib,
            sectorSize: 4096);
        var plan = a.Targets[0].Plan;
        var helperLength = plan.Partitions.Single(p => p.Role == PartitionRole.UefiNtfs).LengthBytes;

        using var written = new MemoryStream(new byte[helperLength]);
        a.Run.ExtraPayloads(a.Targets[0]).Single().Write(written);
        var (result, _) = await WindowsWriteKit.RunAsync(Steps(a));

        Assert.Equal(JobOutcome.Succeeded, result.Outcome);
        Assert.NotEqual(UefiNtfsImage.ForSectorSize(512), written.ToArray());
        Assert.Equal(written.ToArray(), a.Ops.VerifiedPartitions.Single().Expected);
    }

    [RequiresXorrisoFact]
    public async Task ATargetWithoutHelperPartition_HasNoExtraPayloads()
    {
        var a = await ArrangeAsync(new TargetOptions { Firmware = TargetFirmware.Uefi });

        Assert.Empty(a.Run.ExtraPayloads(a.Targets[0]));
    }

    [RequiresXorrisoFact]
    public async Task SeveralTargets_AreCopiedOneAfterTheOther_AndShareTheBar()
    {
        var a = await ArrangeAsync(targets: 2);

        var (result, reports) = await WindowsWriteKit.RunAsync(Steps(a));

        Assert.True(result.Outcome == JobOutcome.Succeeded, result.Error?.ToString());
        foreach (var target in a.Targets)
        {
            AssertMediumHolds(_kit.MainRoot(target), a.Files);
        }

        var total = a.Files.Values.Sum(content => (long)content.Length);
        var copy = reports.Where(r => r.StepKey == WindowsSetupWriter.CopyKey && r.BytesTotal > 0).ToList();
        Assert.Equal(2 * total, copy[^1].BytesTotal);
        Assert.Equal(copy.Select(r => r.BytesDone).Order(), copy.Select(r => r.BytesDone));
        Assert.Equal(["flush", "flush", "drop-caches", "drop-caches"], a.Ops.Calls);
    }

    [RequiresXorrisoFact]
    public async Task AMediumThatDoesNotFit_IsRefusedBeforeAnythingIsWritten()
    {
        var files = WindowsWriteKit.SetupFiles();
        files["sources/huge.bin"] = WindowsWriteKit.Random(70 * 1024 * 1024, 99);
        var a = await ArrangeAsync(deviceBytes: 64 * WindowsWriteKit.Mib, files: files);

        var (result, _) = await WindowsWriteKit.RunAsync(Steps(a));

        Assert.Equal(JobOutcome.Failed, result.Outcome);
        var error = Assert.IsType<BootrixException>(result.Error);
        Assert.Equal(ErrorCode.DeviceTooSmall, error.Code);
        Assert.Equal(WindowsSetupWriter.SourceKey, result.FailedStep);
        Assert.Empty(a.Ops.Calls);
        Assert.Empty(Directory.GetFileSystemEntries(_kit.MainRoot(a.Targets[0])));
    }

    [RequiresXorrisoFact]
    public async Task AnImageThatIsNoDiscImage_IsRefused()
    {
        var a = await ArrangeAsync(inspect: inspection => inspection with { Container = ImageContainer.RawDisk });

        var (result, _) = await WindowsWriteKit.RunAsync(Steps(a));

        Assert.Equal(JobOutcome.Failed, result.Outcome);
        Assert.Equal(ErrorCode.ImageUnsupported, Assert.IsType<BootrixException>(result.Error).Code);
    }

    [RequiresXorrisoFact]
    public async Task Customizers_RunAfterTheReadBack_OnceForEveryTarget_WithTheirOwnFolders()
    {
        var first = new FakeCustomizer("first");
        var second = new FakeCustomizer("second");
        var a = await ArrangeAsync(targets: 2, customizers: [first, second]);

        var (result, reports) = await WindowsWriteKit.RunAsync(Steps(a));

        Assert.True(result.Outcome == JobOutcome.Succeeded, result.Error?.ToString());
        foreach (var customizer in new[] { first, second })
        {
            Assert.Equal(2, customizer.Applied.Count);
            Assert.All(customizer.WorkFolderExisted, Assert.True);
            Assert.Equal(a.Targets.Select(t => _kit.MainRoot(t)), customizer.Applied.Select(c => c.MediaRoot));
            Assert.All(customizer.Applied, c =>
            {
                Assert.Equal(a.Context.Image.Arch, c.Arch);
                Assert.Equal(a.Context.Image.WindowsBuild, c.Build);
                Assert.Same(a.Context, c.Write);
            });
        }

        var folders = first.Applied.Concat(second.Applied).Select(c => c.WorkDirectory).ToList();
        Assert.Equal(folders.Count, folders.Distinct().Count());

        var customize = reports.Where(r => r.StepKey == WindowsSetupWriter.CustomizeKey).Select(r => r.StepFraction).ToList();
        Assert.Equal(customize.Order(), customize);
        Assert.Contains("drop-caches", a.Ops.Calls);
    }

    [RequiresXorrisoFact]
    public async Task TheWorkFolder_IsGoneWhenTheJobEnds()
    {
        var a = await ArrangeAsync();

        await WindowsWriteKit.RunAsync(Steps(a));

        Assert.False(Directory.Exists(a.Context.WorkDirectory));
    }

    [RequiresXorrisoFact]
    public async Task Cancelling_DuringTheCopy_EndsTheJobAsCanceled()
    {
        var files = WindowsWriteKit.SetupFiles();
        files["sources/big.bin"] = WindowsWriteKit.Random(20 * 1024 * 1024, 5);
        var a = await ArrangeAsync(files: files);
        using var cts = new CancellationTokenSource();
        var runner = new JobRunner(Microsoft.Extensions.Logging.Abstractions.NullLogger<JobRunner>.Instance);

        var result = await runner.RunAsync(
            new Job("cancel", "cancel", Steps(a)),
            new DelegateProgressSink(report =>
            {
                if (report.StepKey == WindowsSetupWriter.CopyKey && report.BytesDone > 0)
                {
                    cts.Cancel();
                }
            }),
            cts.Token);

        Assert.Equal(JobOutcome.Canceled, result.Outcome);
        Assert.False(Directory.Exists(a.Context.WorkDirectory));
    }

    [RequiresXorrisoFact]
    public async Task AnImageWithOnlyA32BitLoader_IsReportedInTheLog()
    {
        var files = WindowsWriteKit.SetupFiles();
        files.Remove("efi/boot/bootx64.efi");
        files["efi/boot/bootia32.efi"] = WindowsWriteKit.Random(30_000, 12);
        var logs = new CapturingLoggerFactory();
        var a = await ArrangeAsync(files: files, loggers: logs, arch: WindowsArch.X86);

        var (result, _) = await WindowsWriteKit.RunAsync(Steps(a));

        Assert.Equal(JobOutcome.Succeeded, result.Outcome);
        Assert.Contains(logs.Messages, message => message.Level == LogLevel.Warning && message.Text.Contains("32-bit UEFI loader", StringComparison.Ordinal));
    }
}

internal sealed class CapturingLoggerFactory : ILoggerFactory
{
    public List<(LogLevel Level, string Text)> Messages { get; } = [];

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public ILogger CreateLogger(string categoryName) => new Capture(Messages);

    public void Dispose()
    {
    }

    private sealed class Capture(List<(LogLevel Level, string Text)> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (messages)
            {
                messages.Add((logLevel, formatter(state, exception)));
            }
        }
    }
}
