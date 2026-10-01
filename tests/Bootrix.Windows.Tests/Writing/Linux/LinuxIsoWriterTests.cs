// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images;
using Bootrix.Core.Model;
using Bootrix.Core.Partitioning;
using Bootrix.Core.Planning;
using Bootrix.Core.Profiles;
using Bootrix.Core.Storage;
using Bootrix.Windows.Writing;
using Bootrix.Windows.Writing.Linux;

namespace Bootrix.Windows.Tests.Writing.Linux;

/// <summary>
/// The decisions of the Linux writer that do not touch a disk: which plans it takes and which steps and partition
/// payloads it creates. The steps themselves need a real disk; the hardware checklist lists what to try by hand.
/// </summary>
public class LinuxIsoWriterTests
{
    private const long Gib = 1024L * 1024 * 1024;

    private static readonly LinuxIsoWriter Writer = new(null!, null!);

    private static ImageProfile Image(ImageKind kind = ImageKind.LinuxIsoOnly, string? family = "debian-live") => new()
    {
        Kind = kind,
        Family = family,
        Container = ImageContainer.Iso9660,
        VolumeLabel = "LIVE",
        TotalBytes = 2 * Gib,
        LargestFileBytes = Gib,
        HasBiosBootFiles = true,
        HasEfiBootFiles = true,
        IsHybrid = kind == ImageKind.LinuxHybrid,
    };

    private static MediaPlan Plan(ImageProfile image, TargetOptions? target = null) =>
        LayoutPlanner.Plan(image, target ?? new TargetOptions { Mode = WriteMode.Extract }, new DeviceCaps { SizeBytes = 16 * Gib });

    [Theory]
    [InlineData(ImageKind.LinuxIsoOnly, "debian-live", true)]
    [InlineData(ImageKind.LinuxHybrid, "ubuntu", true)]
    [InlineData(ImageKind.LinuxIsoOnly, null, true)]
    [InlineData(ImageKind.OtherOs, "esxi", true)]
    [InlineData(ImageKind.OtherOs, "reactos", false)]
    [InlineData(ImageKind.OtherOs, "kolibrios", false)]
    [InlineData(ImageKind.WindowsSetup, "windows", false)]
    [InlineData(ImageKind.WindowsPe, "winpe", false)]
    [InlineData(ImageKind.Dos, "freedos", false)]
    public void CanWrite_TakesLinuxAndEsxiInExtractMode(ImageKind kind, string? family, bool expected)
    {
        var image = Image(kind, family);
        var plan = kind is ImageKind.Dos
            ? new MediaPlan { WriteMethod = WriteMethod.FormatOnly, Scheme = PartitionScheme.Mbr }
            : Plan(image);

        Assert.Equal(expected, Writer.CanWrite(plan, image));
    }

    [Fact]
    public void CanWrite_RawCopyOfAHybridImage_IsLeftToTheRawWriter()
    {
        var image = Image(ImageKind.LinuxHybrid, "ubuntu");
        var plan = Plan(image, new TargetOptions { Mode = WriteMode.RawCopy });

        Assert.Equal(WriteMethod.RawCopy, plan.WriteMethod);
        Assert.False(Writer.CanWrite(plan, image));
    }

    [Fact]
    public void CanWrite_SuperfloppyPlan_IsRefused()
    {
        var image = Image();
        var plan = Plan(image) with { Superfloppy = true };

        Assert.False(Writer.CanWrite(plan, image));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CreateSteps_FollowsTheOrderOfTheStandardSteps_AndVerifiesOnlyWhenAsked(bool readBack)
    {
        var image = Image();
        var context = new MediaWriteContext
        {
            JobId = "write-test",
            ImagePath = "live.iso",
            Inspection = new ImageInspection { Profile = image, Container = ImageContainer.Iso9660 },
            Spec = new JobSpec { Verify = new VerifyOptions { ReadBack = readBack } },
            Targets = [],
            WorkDirectory = Path.GetTempPath(),
        };

        var keys = Writer.CreateSteps(context).Select(step => step.Key).ToList();

        string[] expected =
        [
            StandardSteps.CheckKey,
            StandardSteps.PrepareKey,
            LinuxIsoWriter.CopyKey,
            LinuxIsoWriter.BootloaderKey,
            .. readBack ? [LinuxIsoWriter.VerifyKey] : Array.Empty<string>(),
            StandardSteps.FinishKey,
        ];
        Assert.Equal(expected, keys);
    }

    [Fact]
    public void ExtraPayloads_PersistencePartition_IsFormattedBeforeWindowsSeesIt()
    {
        var image = Image(family: "debian-live");
        var plan = Plan(image, new TargetOptions { Mode = WriteMode.Extract, PersistenceMegabytes = 128 });
        var payloads = PayloadsOf(image, plan);

        var persistence = plan.Partitions.ToList().FindIndex(p => p.Role == PartitionRole.Persistence);
        var payload = Assert.Single(payloads);
        Assert.Equal(persistence, payload.PartitionIndex);

        // The payload writes an ext3 file system into whatever stream it is given.
        var partition = plan.Partitions[persistence];
        using var stream = new MemoryStream();
        stream.SetLength(partition.LengthBytes);
        payload.Write(stream);
        Assert.Equal(0xEF53, BitConverter.ToUInt16(stream.ToArray(), 1024 + 56));
    }

    [Fact]
    public void ExtraPayloads_UefiNtfsPartition_GetsTheHelperImage()
    {
        var image = Image(family: "debian-live") with { LargestFileBytes = 5 * Gib, TotalBytes = 7 * Gib };
        var plan = Plan(image);
        var payloads = PayloadsOf(image, plan);

        Assert.True(plan.UsesUefiNtfs);
        var helper = plan.Partitions.ToList().FindIndex(p => p.Role == PartitionRole.UefiNtfs);
        var payload = Assert.Single(payloads);
        Assert.Equal(helper, payload.PartitionIndex);

        using var stream = new MemoryStream(new byte[1024 * 1024]);
        payload.Write(stream);
        var written = stream.ToArray();
        Assert.Equal(0x55, written[510]);
        Assert.Equal(0xAA, written[511]);
        Assert.Equal(Bootrix.Core.Writing.Linux.UefiNtfsHelper.Image(), written);
    }

    [Fact]
    public void ExtraPayloads_PlainMedium_HasNone()
    {
        var image = Image();

        Assert.Empty(PayloadsOf(image, Plan(image)));
    }

    [Fact]
    public void MountedPartitions_LeavesOutTheExtPartition_SoWindowsIsNotWaitedFor()
    {
        var image = Image();
        var plan = Plan(image, new TargetOptions { Mode = WriteMode.Extract, PersistenceMegabytes = 128 });

        var mounted = PlanLayout.MountedPartitions(plan);

        var persistence = plan.Partitions.ToList().FindIndex(p => p.Role == PartitionRole.Persistence);
        Assert.DoesNotContain(persistence, mounted);
        Assert.Contains(plan.Partitions.ToList().FindIndex(p => p.Role == PartitionRole.Main), mounted);
    }

    private static IReadOnlyList<Bootrix.Windows.Storage.PartitionPayload> PayloadsOf(ImageProfile image, MediaPlan plan)
    {
        var device = new StorageDevice { DiskNumber = 9, DevicePath = @"\\?\test" };
        var context = new MediaWriteContext
        {
            JobId = "write-test",
            ImagePath = "live.iso",
            Inspection = new ImageInspection { Profile = image, Container = ImageContainer.Iso9660 },
            Spec = new JobSpec(),
            Targets = [new MediaWriteTarget { Device = device, Identity = new DiskIdentity { DevicePath = device.DevicePath }, Plan = plan }],
            WorkDirectory = Path.GetTempPath(),
        };

        return new LinuxWriteRun(null!, null!, context).ExtraPayloads(context.Targets[0]);
    }
}
