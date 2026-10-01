// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Model;
using Bootrix.Core.Profiles;

namespace Bootrix.Core.Tests.Profiles;

public sealed class ProfileStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bootrix-profiles-" + Guid.NewGuid().ToString("N")[..10]);
    private string? _team;

    private string UserDirectory => Path.Combine(_root, "user");

    private string TeamDirectory => Path.Combine(_root, "team");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private ProfileStore Store() => new(UserDirectory, () => _team);

    private static JobSpec Spec() => new()
    {
        Target = new TargetOptions { Scheme = PartitionScheme.Gpt, FileSystem = FileSystemKind.Ntfs, Label = "WERKSTATT" },
        Windows = new WindowsSetupOptions { BypassTpm = true, LocalAccountName = "Techniker" },
        Verify = new VerifyOptions { ReadBack = false },
    };

    [Fact]
    public void ASavedProfileComesBackAsTheSameSettings()
    {
        var store = Store();

        store.Save("Standard-Stick", Spec(), "Für Neukunden");
        var resolved = store.Resolve("Standard-Stick");

        Assert.Equal(PartitionScheme.Gpt, resolved.Spec.Target.Scheme);
        Assert.Equal(FileSystemKind.Ntfs, resolved.Spec.Target.FileSystem);
        Assert.Equal("WERKSTATT", resolved.Spec.Target.Label);
        Assert.True(resolved.Spec.Windows.BypassTpm);
        Assert.Equal("Techniker", resolved.Spec.Windows.LocalAccountName);
        Assert.False(resolved.Spec.Verify.ReadBack);
        var entry = Assert.Single(store.List());
        Assert.Equal(("Standard-Stick", ProfileOrigin.User, "Für Neukunden"), (entry.Name, entry.Origin, entry.Description));
    }

    [Fact]
    public void ThePathOfTheImageIsNotPartOfAProfile()
    {
        var store = Store();

        store.Save("Mit Abbild", Spec() with { Source = new ImageReference { Path = @"C:\iso\win.iso" } });

        Assert.Null(store.Resolve("Mit Abbild").Spec.Source);
    }

    [Fact]
    public void SavingAgainReplacesTheProfile()
    {
        var store = Store();
        store.Save("P", Spec());

        store.Save("P", Spec() with { Target = new TargetOptions { Label = "NEU" } });

        Assert.Equal("NEU", store.Resolve("P").Spec.Target.Label);
        Assert.Single(store.List());
        Assert.Empty(Directory.GetFiles(UserDirectory, "*.tmp"));
    }

    [Fact]
    public void TeamProfilesAreListedAndAPersonalOneWithTheSameNameWins()
    {
        _team = TeamDirectory;
        var teamStore = new ProfileStore(TeamDirectory, () => null);
        teamStore.Save("Kunde", Spec() with { Target = new TargetOptions { Label = "TEAM" } });
        teamStore.Save("Nur Team", Spec());
        var store = Store();

        Assert.Equal(["Kunde", "Nur Team"], store.List().Select(e => e.Name));
        Assert.All(store.List(), e => Assert.Equal(ProfileOrigin.Team, e.Origin));
        Assert.Equal("TEAM", store.Resolve("Kunde").Spec.Target.Label);

        store.Save("Kunde", Spec() with { Target = new TargetOptions { Label = "MEINS" } });

        Assert.Equal("MEINS", store.Resolve("Kunde").Spec.Target.Label);
        Assert.Equal(ProfileOrigin.User, store.List().Single(e => e.Name == "Kunde").Origin);
    }

    [Fact]
    public void OnlyPersonalProfilesCanBeDeleted()
    {
        _team = TeamDirectory;
        new ProfileStore(TeamDirectory, () => null).Save("Team", Spec());
        var store = Store();
        store.Save("Eigen", Spec());

        Assert.True(store.Delete("Eigen"));
        Assert.False(store.Delete("Eigen"));
        Assert.False(store.Delete("Team"));
        Assert.Equal(["Team"], store.List().Select(e => e.Name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("con?")]
    [InlineData("ende.")]
    public void NamesThatCannotBeFileNamesAreRefused(string name)
    {
        Assert.Throws<BootrixException>(() => Store().Save(name, Spec()));
    }

    [Fact]
    public void AnUnreadableProfileIsStillListed()
    {
        Directory.CreateDirectory(UserDirectory);
        File.WriteAllText(Path.Combine(UserDirectory, "kaputt" + DirectoryProfileSource.Extension), "{ nope");

        var entry = Assert.Single(Store().List());

        Assert.Equal("kaputt", entry.Name);
    }

    [Fact]
    public void ALockedSettingWinsOverWhatTheFormSays()
    {
        _team = TeamDirectory;
        var teamPath = Path.Combine(TeamDirectory, "Gesperrt" + DirectoryProfileSource.Extension);
        Directory.CreateDirectory(TeamDirectory);
        File.WriteAllText(teamPath, """
            { "name": "Gesperrt", "locked": ["windows.bypassTpm", "target.scheme"],
              "spec": { "windows": { "bypassTpm": false }, "target": { "scheme": "Mbr" } } }
            """);
        var resolved = Store().Resolve("Gesperrt");

        var enforced = ProfileLocks.Enforce(Spec(), resolved);

        Assert.False(enforced.Windows.BypassTpm);
        Assert.Equal(PartitionScheme.Mbr, enforced.Target.Scheme);
        Assert.Equal("Techniker", enforced.Windows.LocalAccountName);
        Assert.Equal(FileSystemKind.Ntfs, enforced.Target.FileSystem);
    }

    [Fact]
    public void WithoutLocksTheSpecIsLeftAlone()
    {
        var store = Store();
        store.Save("Offen", Spec());

        var spec = Spec();

        Assert.Same(spec, ProfileLocks.Enforce(spec, store.Resolve("Offen")));
    }
}
