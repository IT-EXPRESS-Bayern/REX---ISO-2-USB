// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.FileSystems.Ext;
using Bootrix.Core.Images.Policy;
using Bootrix.Core.Model;
using Bootrix.Core.Partitioning;
using Bootrix.Core.Planning;
using Bootrix.Core.Tests.Tooling;
using Bootrix.Core.Writing.Linux;

namespace Bootrix.Core.Tests.Writing.Linux;

public class PersistenceStoreTests
{
    private const long Mib = 1024 * 1024;

    private static PlannedPartition Partition(string label, long length = 64 * Mib) => new()
    {
        Role = PartitionRole.Persistence,
        StartBytes = Mib,
        LengthBytes = length,
        FileSystem = FileSystemKind.Ext3,
        Label = label,
        MbrType = MbrPartitionType.Linux,
        GptType = GptTypes.LinuxData,
    };

    private static string Format(PlannedPartition partition, FamilyTraits traits)
    {
        var path = Path.Combine(Path.GetTempPath(), "bootrix-persist-" + Guid.NewGuid().ToString("N")[..8] + ".img");
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite);
        stream.SetLength(partition.LengthBytes);
        PersistenceStore.Format(stream, partition, traits);
        return path;
    }

    [Fact]
    public void OptionsFor_LiveBoot_CarriesThePersistenceConfigWithTheFinalLineFeed()
    {
        var options = PersistenceStore.OptionsFor(Partition("persistence"), ImagePolicy.Default.TraitsOf("debian-live"));

        var file = Assert.Single(options.RootFiles);
        Assert.Equal("persistence.conf", file.Name);
        Assert.Equal("/ union\n", System.Text.Encoding.ASCII.GetString(file.Content.Span));
        Assert.Equal(ExtFileSystemType.Ext3, options.Type);
        Assert.Equal("persistence", options.Label);
    }

    [Fact]
    public void OptionsFor_Casper_HasNoConfigFile()
    {
        var options = PersistenceStore.OptionsFor(Partition("writable"), ImagePolicy.Default.TraitsOf("ubuntu"));

        Assert.Empty(options.RootFiles);
        Assert.Equal("writable", options.Label);
    }

    [Theory]
    [InlineData("debian-live", "persistence")]
    [InlineData("kali", "persistence")]
    [InlineData("ubuntu", "writable")]
    [InlineData("linuxmint", "writable")]
    [InlineData("popos", "writable")]
    public void PlannerLabel_FollowsTheFamilysPersistenceMechanism(string family, string label)
    {
        Assert.Equal(label, LinuxFamiliesLabel(family));
    }

    private static string LinuxFamiliesLabel(string family)
    {
        var image = new Bootrix.Core.Images.ImageProfile
        {
            Kind = Bootrix.Core.Images.ImageKind.LinuxIsoOnly,
            Family = family,
            VolumeLabel = "X",
            TotalBytes = 1024 * Mib,
            LargestFileBytes = 100 * Mib,
            HasBiosBootFiles = true,
        };
        var plan = LayoutPlanner.Plan(image, new Bootrix.Core.Profiles.TargetOptions { Mode = WriteMode.Extract, PersistenceMegabytes = 64 }, new DeviceCaps { SizeBytes = 8L * 1024 * Mib });
        return plan.Partitions.Single(p => p.Role == PartitionRole.Persistence).Label!;
    }

    [RequiresToolFact("e2fsck", "dumpe2fs", "debugfs")]
    public void Format_LiveBoot_PassesE2fsckAndHoldsTheConfig()
    {
        var path = Format(Partition("persistence"), ImagePolicy.Default.TraitsOf("debian-live"));
        try
        {
            var fsck = ExternalTools.Run("e2fsck", "-fn", path);
            Assert.True(fsck.ExitCode == 0, fsck.Combined);
            Assert.Contains("Filesystem volume name:   persistence", ExternalTools.Run("dumpe2fs", "-h", path).Combined, StringComparison.Ordinal);
            Assert.Equal("/ union\n", ExternalTools.Run("debugfs", "-R", "cat /persistence.conf", path).Output);
            Assert.Contains("has_journal", ExternalTools.Run("dumpe2fs", "-h", path).Combined, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [RequiresToolFact("e2fsck", "dumpe2fs")]
    public void Format_Casper_PassesE2fsckWithTheWritableLabel()
    {
        var path = Format(Partition("writable", 200 * Mib), ImagePolicy.Default.TraitsOf("ubuntu"));
        try
        {
            var fsck = ExternalTools.Run("e2fsck", "-fn", path);
            Assert.True(fsck.ExitCode == 0, fsck.Combined);
            Assert.Contains("Filesystem volume name:   writable", ExternalTools.Run("dumpe2fs", "-h", path).Combined, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [RequiresToolFact("e2fsck")]
    public void Format_SmallestStore_PassesE2fsck()
    {
        var path = Format(Partition("persistence", 16 * Mib), ImagePolicy.Default.TraitsOf("kali"));
        try
        {
            var fsck = ExternalTools.Run("e2fsck", "-fn", path);
            Assert.True(fsck.ExitCode == 0, fsck.Combined);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
