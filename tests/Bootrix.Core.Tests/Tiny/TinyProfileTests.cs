// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Tiny;

namespace Bootrix.Core.Tests.Tiny;

public class TinyProfileTests
{
    public static TheoryData<string> Ids => new(TinyProfiles.BuiltInIds);

    [Theory]
    [MemberData(nameof(Ids))]
    public void ProfileLoads(string id)
    {
        var profile = TinyProfiles.Load(id);

        Assert.Equal(id, profile.Id);
        Assert.NotEmpty(profile.Appx);
        Assert.NotEmpty(profile.Registry);
        Assert.False(string.IsNullOrWhiteSpace(profile.Description));
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void NoDuplicateAppxEntries(string id)
    {
        var names = TinyProfiles.Load(id).Appx.Select(a => a.Name.ToLowerInvariant()).ToList();

        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void EveryReferencedGroupIsDefined(string id)
    {
        var profile = TinyProfiles.Load(id);
        var defined = profile.Groups.Select(g => g.Id).ToHashSet();
        var used = profile.Appx.Select(a => a.Group)
            .Concat(profile.Packages.Select(p => p.Group))
            .Concat(profile.Capabilities.Select(c => c.Group))
            .Concat(profile.Files.Select(f => f.Group))
            .Concat(profile.Registry.Select(r => r.Group))
            .Concat(profile.BootWimRegistry.Select(r => r.Group))
            .Where(g => g is not null)
            .Distinct();

        Assert.All(used, g => Assert.Contains(g!, defined));
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void RegistryEntriesAreWellFormed(string id)
    {
        var profile = TinyProfiles.Load(id);

        foreach (var change in profile.Registry.Concat(profile.BootWimRegistry))
        {
            Assert.False(string.IsNullOrWhiteSpace(change.Key));
            Assert.DoesNotContain("HKLM", change.Key, StringComparison.OrdinalIgnoreCase);
            Assert.False(change.Key.StartsWith('\\'));
            if (change.Action == RegistryAction.SetValue)
            {
                Assert.False(string.IsNullOrEmpty(change.Name), change.Key);
                Assert.NotNull(change.Value);
                if (change.Kind == RegistryValueKind.DWord)
                {
                    Assert.True(uint.TryParse(change.Value, out _), $"{change.Key}\\{change.Name} = {change.Value}");
                }
            }
            else if (change.Action == RegistryAction.DeleteValue)
            {
                Assert.False(string.IsNullOrEmpty(change.Name));
            }
        }
    }

    [Theory]
    [MemberData(nameof(Ids))]
    public void FilePathsAreRelativeAndStayInsideTheImage(string id)
    {
        foreach (var file in TinyProfiles.Load(id).Files)
        {
            Assert.False(Path.IsPathRooted(file.Path), file.Path);
            Assert.DoesNotContain("..", file.Path, StringComparison.Ordinal);
            Assert.DoesNotContain('/', file.Path);
            Assert.NotEqual("Windows", file.Path.TrimEnd('\\'));
            Assert.NotEqual(@"Windows\System32", file.Path.TrimEnd('\\'));
        }
    }

    [Fact]
    public void Tiny11KeepsWindowsTerminalAndUpdateAndDefender()
    {
        var profile = TinyProfiles.Load("tiny11");

        Assert.DoesNotContain(profile.Appx, a => a.Name.Contains("WindowsTerminal", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(profile.Registry, r => r.Group == "windows-update" || r.Group == "defender");
        Assert.Empty(profile.Packages);
        Assert.Null(profile.WinSxs);
        Assert.False(profile.BreaksServicing);
    }

    [Fact]
    public void Tiny11CoreBuildsOnTiny11AndAddsTheDestructiveParts()
    {
        var tiny11 = TinyProfiles.Load("tiny11");
        var core = TinyProfiles.Load("tiny11core");

        Assert.True(core.BreaksServicing);
        Assert.True(core.EmptyWinRe);
        Assert.Equal(tiny11.Appx.Count, core.Appx.Count);
        Assert.True(core.Registry.Count > tiny11.Registry.Count);
        Assert.Contains(core.Registry, r => r.Group == "windows-update");
        Assert.Contains(core.Registry, r => r.Group == "defender");
        Assert.Contains(core.Packages, p => p.Pattern.Contains("{lang}", StringComparison.Ordinal));
        Assert.NotNull(core.WinSxs);
        Assert.Contains("Manifests", core.WinSxs.KeepByArchitecture["amd64"]);
        Assert.Contains("Manifests", core.WinSxs.KeepByArchitecture["arm64"]);
    }

    [Fact]
    public void WinSxsWhitelistsHaveNoDuplicates()
    {
        var core = TinyProfiles.Load("tiny11core");

        foreach (var (arch, keep) in core.WinSxs!.KeepByArchitecture)
        {
            Assert.True(keep.Count == keep.Distinct(StringComparer.OrdinalIgnoreCase).Count(), arch);
        }
    }

    [Fact]
    public void Tiny10IsForWindows10AndKeepsEdgeByDefault()
    {
        var profile = TinyProfiles.Load("tiny10");

        Assert.Equal("10", profile.WindowsFamily);
        Assert.False(profile.Groups.Single(g => g.Id == "edge").Default);
        Assert.Contains(profile.Files, f => f.Path.EndsWith("SysWOW64\\OneDriveSetup.exe", StringComparison.Ordinal));
        Assert.Empty(profile.BootWimRegistry);
        Assert.Contains(profile.Capabilities, c => c.Pattern == "Browser.InternetExplorer");
    }

    [Fact]
    public void UnknownProfileIsReported()
    {
        Assert.Throws<Bootrix.Core.Errors.BootrixException>(() => TinyProfiles.Load("tiny12"));
    }

    [Fact]
    public void DisabledGroups_StartsWithTheGroupsThatAreOffByDefault()
    {
        var profile = TinyProfiles.Load("tiny11");

        var disabled = TinyProfiles.DisabledGroups(profile, [], []);

        Assert.Equal(profile.Groups.Where(g => !g.Default).Select(g => g.Id).Order(StringComparer.Ordinal), disabled.Order(StringComparer.Ordinal));
        Assert.Contains("tools", disabled);
    }

    [Fact]
    public void DisabledGroups_IncludeSwitchesADefaultOffGroupOnAndKeepSwitchesOneOff()
    {
        var profile = TinyProfiles.Load("tiny11");

        var disabled = TinyProfiles.DisabledGroups(profile, ["EDGE"], ["tools"]);

        Assert.DoesNotContain("tools", disabled);
        Assert.Contains("edge", disabled);
    }

    [Fact]
    public void DisabledGroups_UnknownNameIsAnErrorInsteadOfASilentTypo()
    {
        var profile = TinyProfiles.Load("tiny11");

        var ex = Assert.Throws<Bootrix.Core.Errors.BootrixException>(() => TinyProfiles.DisabledGroups(profile, ["edgee"], []));

        Assert.Equal(Bootrix.Core.Errors.ErrorCode.InvalidSpec, ex.Code);
    }
}
