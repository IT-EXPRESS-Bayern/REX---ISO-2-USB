// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;
using Bootrix.Core.Profiles;
using Bootrix.Core.Tiny;
using Bootrix.Core.Unattend;
using Bootrix.Core.Writing.Windows.Customization;

namespace Bootrix.Core.Tests.Writing.Windows;

public partial class HardwareCheckBypassTests
{
    private static readonly WindowsSetupOptions Everything = new()
    {
        BypassTpm = true,
        BypassSecureBoot = true,
        BypassRam = true,
        BypassCpu = true,
        BypassStorage = true,
    };

    [Fact]
    public void IsRequested_NoneSet_IsFalse()
    {
        Assert.False(HardwareCheckBypass.IsRequested(new WindowsSetupOptions()));
        Assert.Empty(HardwareCheckBypass.Changes(new WindowsSetupOptions()));
    }

    [Theory]
    [InlineData(nameof(WindowsSetupOptions.BypassTpm), "BypassTPMCheck")]
    [InlineData(nameof(WindowsSetupOptions.BypassSecureBoot), "BypassSecureBootCheck")]
    [InlineData(nameof(WindowsSetupOptions.BypassRam), "BypassRAMCheck")]
    [InlineData(nameof(WindowsSetupOptions.BypassCpu), "BypassCPUCheck")]
    [InlineData(nameof(WindowsSetupOptions.BypassStorage), "BypassStorageCheck")]
    public void Changes_OneFlag_SetsExactlyItsValue(string flag, string value)
    {
        var options = flag switch
        {
            nameof(WindowsSetupOptions.BypassTpm) => new WindowsSetupOptions { BypassTpm = true },
            nameof(WindowsSetupOptions.BypassSecureBoot) => new WindowsSetupOptions { BypassSecureBoot = true },
            nameof(WindowsSetupOptions.BypassRam) => new WindowsSetupOptions { BypassRam = true },
            nameof(WindowsSetupOptions.BypassCpu) => new WindowsSetupOptions { BypassCpu = true },
            _ => new WindowsSetupOptions { BypassStorage = true },
        };

        var change = Assert.Single(HardwareCheckBypass.Changes(options));

        Assert.True(HardwareCheckBypass.IsRequested(options));
        Assert.Equal(RegistryHive.System, change.Hive);
        Assert.Equal(@"Setup\LabConfig", change.Key);
        Assert.Equal(value, change.Name);
        Assert.Equal(RegistryValueKind.DWord, change.Kind);
        Assert.Equal("1", change.Value);
        Assert.Equal(RegistryAction.SetValue, change.Action);
    }

    [Fact]
    public void Changes_AreTheSameValuesTheAnswerFileSets()
    {
        var options = new UnattendOptions { Windows = Everything };
        var xml = UnattendBuilder.ToXml(options);

        var inAnswerFile = RegAdd().Matches(xml).Select(m => m.Groups[1].Value).Order().ToList();
        var inRegistry = HardwareCheckBypass.Changes(Everything).Select(c => c.Name!).Order().ToList();

        Assert.Equal(5, inRegistry.Count);
        Assert.Equal(inAnswerFile, inRegistry);
    }

    [Fact]
    public void Changes_AreAmongTheValuesTheTinyProfilesSetInTheSetupImage()
    {
        var profile = TinyProfiles.Load("tiny11");
        var tinyValues = profile.BootWimRegistry
            .Where(c => c is { Hive: RegistryHive.System, Key: @"Setup\LabConfig" })
            .Select(c => (c.Name, c.Value, c.Kind))
            .ToList();

        var ours = HardwareCheckBypass.Changes(Everything).Select(c => (c.Name, c.Value, c.Kind)).ToList();

        Assert.NotEmpty(tinyValues);
        Assert.All(ours, change => Assert.Contains(change, tinyValues));
    }

    [Fact]
    public void Changes_ApplyThroughTheSharedApplier_ToTheSystemHive()
    {
        var tools = new Support.FakeImageTools();

        RegistryChangeApplier.Apply(tools, "/mount", HardwareCheckBypass.Changes(Everything), CancellationToken.None);

        Assert.Equal(5, tools.Registry.Count);
        Assert.All(tools.Registry, entry => Assert.StartsWith(@"System:Setup\LabConfig\Bypass", entry, StringComparison.Ordinal));
        Assert.Equal(["load-hive:System", "unload-hive:System"], tools.Calls);
    }

    [GeneratedRegex(@"reg add HKLM\\SYSTEM\\Setup\\LabConfig /v (\w+) /t REG_DWORD /d 1 /f")]
    private static partial Regex RegAdd();
}
